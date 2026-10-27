"""End-to-end authorization, membership, local login and antiforgery checks; stdlib only."""
import html
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo
import http.cookiejar
import json
import re
import urllib.error
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:8080'
PASSWORD = 'Integration-only-password-335!'

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

class Browser:
    def __init__(self):
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), NoRedirect())
    def request(self, path, fields=None):
        data = None if fields is None else urllib.parse.urlencode(fields).encode()
        request = urllib.request.Request(BASE + path, data=data)
        try:
            response = self.opener.open(request, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.read().decode(), response.headers
    def post(self, page, endpoint, fields):
        status, text, _ = self.request(page)
        assert status == 200, (page, status)
        token = re.search(r'name="__RequestVerificationToken"\s+value="([^"]+)"', text)
        assert token, f'Missing antiforgery token on {page}'
        return self.request(endpoint, dict(fields, __RequestVerificationToken=html.unescape(token[1])))

def check(condition, message):
    assert condition, message
    print('PASS', message)

def register(browser, email, name):
    status, _, headers = browser.post('/login', '/auth/register', {
        'email': email, 'name': name, 'password': PASSWORD})
    check(status == 302 and headers['Location'] == '/', 'register and sign in ' + name)

def login(browser, email):
    status, _, headers = browser.post('/login', '/auth/login', {'email': email, 'password': PASSWORD})
    check(status == 302 and headers['Location'] == '/', 'password login')

anon, admin, member, newcomer = (Browser() for _ in range(4))
status, text, _ = anon.request('/leaderboards/1')
check(status == 200 and 'Original athlete' in text, 'public board retains original starter participant')
check(anon.request('/leaderboards/save', {})[0] == 302, 'anonymous edits rejected')
check(anon.request('/leaderboards/1/members/update', {})[0] == 302, 'anonymous membership changes rejected')
register(admin, 'admin@example.test', 'Alice Admin')
register(member, 'member@example.test', 'Bob Member')
status, text, _ = anon.request('/leaderboards/1')
check(status == 200 and 'Alice Admin' in text and 'Bob Member' in text, 'all accounts appear publicly without workouts')
check('admin@example.test' not in text and 'member@example.test' not in text, 'emails stay private')
check(member.request('/leaderboards/1/members')[0] == 403, 'membership screen restricted to administrator')
check(member.request('/leaderboards/1/members/update', {'participantId': '2', 'action': 'exclude'})[0] == 302,
      'non-admin membership POST rejected')
check(member.request('/leaderboards/save', {'id': '1', 'name': 'CSRF attack'})[0] == 400,
      'edits require valid antiforgery token')
check(admin.request('/leaderboards/1/members/update', {'participantId': '3', 'action': 'exclude'})[0] == 400,
      'administrator membership changes also require antiforgery')

board = {'id': '0', 'name': 'Community Challenge', 'description': 'Created by a member',
         'startsOn': '2020-01-01', 'endsOn': '2099-12-31', 'cutoff': '10:30', 'minimumMinutes': '30'}
status, _, headers = member.post('/leaderboards/new', '/leaderboards/save', board)
check(status == 302, 'any signed-in member can create a board')
board_url = headers['Location']
board_id = board_url.rsplit('/', 1)[1]
status, text, _ = anon.request(board_url)
check(status == 200 and 'Bob Member' in text and 'Alice Admin' in text, 'new boards automatically include existing accounts')
board.update(id=board_id, name='Edited by another user')
status, _, _ = admin.post(board_url + '/edit', '/leaderboards/save', board)
check(status == 302 and 'Edited by another user' in anon.request(board_url)[1], 'another signed-in user can edit the board')

for attempt in range(2):
    status, _, _ = admin.post('/leaderboards/1/members', '/leaderboards/1/members/update',
                              {'participantId': '3', 'action': 'exclude'})
    check(status == 302, 'administrator removal is idempotent')
check('Bob Member' not in anon.request('/leaderboards/1')[1], 'removed participant disappears publicly')
check('Bob Member' in anon.request(board_url)[1], 'removal applies only to the selected board')
check(member.post('/login', '/auth/logout', {})[0] == 302, 'logout')
login(member, 'MEMBER@example.test')
check('Bob Member' not in anon.request('/leaderboards/1')[1], 'logging back in does not undo administrator removal')
register(newcomer, 'new@example.test', 'Charlie New')
check(all('Charlie New' in anon.request(url)[1] for url in ['/leaderboards/1', board_url]),
      'new account appears on every existing board')
status, _, _ = admin.post('/leaderboards/1/members', '/leaderboards/1/members/update',
                          {'participantId': '3', 'action': 'include'})
check(status == 302 and 'Bob Member' in anon.request('/leaderboards/1')[1], 'administrator restores participant')
invalid_board = dict(board, endsOn='2019-01-01')
check(member.post(board_url + '/edit', '/leaderboards/save', invalid_board)[0] == 400, 'invalid date range rejected')
check(anon.request('/leaderboards/999999')[0] == 404, 'missing leaderboard returns 404')
today = datetime.now(ZoneInfo('Europe/Warsaw')).date()
monday = today - timedelta(days=today.weekday())
previous = monday - timedelta(days=7)
def total_for(text, name):
    row = next(row for row in re.findall(r'<tr>(.*?)</tr>', text, re.S) if name + '</b>' in row)
    return int(re.search(r'<strong>(\d+)</strong>', row)[1])
status, current_text, _ = anon.request('/leaderboards/1')
check(status == 200 and total_for(current_text, 'Original athlete') == 6, 'current week includes only current-week workout')
previous_url = '/leaderboards/1?week=' + previous.isoformat()
check(previous_url in html.unescape(current_text) and 'rel="next"' not in current_text,
      'current week links to previous week but not to the future')
status, history_text, _ = anon.request(previous_url)
check(status == 200 and total_for(history_text, 'Original athlete') == 7 and 'rel="next"' in history_text and 'Current week' in history_text,
      'previous-week URL loads historical scores and provides forward/current navigation')
check(total_for(anon.request('/leaderboards/1?week=' + (previous + timedelta(days=2)).isoformat())[1], 'Original athlete') == 7,
      'midweek date selects the containing week')
check(total_for(anon.request('/leaderboards/1?week=9999-12-31')[1], 'Original athlete') == 6,
      'future week query is safely limited to current week')
check(total_for(anon.request('/leaderboards/1?week=bad-date')[1], 'Original athlete') == 6,
      'invalid week query falls back without a server error')
first_text = anon.request('/leaderboards/1?week=0001-01-01')[1]
check('rel="prev"' not in first_text and 'Week 1 of' in first_text, 'first leaderboard week prevents earlier navigation')
ended_board = dict(board, id='0', name='Ended history challenge', startsOn=(previous - timedelta(days=7)).isoformat(),
                   endsOn=(previous + timedelta(days=2)).isoformat(), minimumMinutes='15', cutoff='11:00')
status, _, headers = member.post('/leaderboards/new', '/leaderboards/save', ended_board)
ended_text = anon.request(headers['Location'])[1]
check(status == 302 and total_for(ended_text, 'Original athlete') == 7 and 'rel="next"' not in ended_text,
      'ended leaderboard opens its final week rather than an empty current week')
future_board = dict(ended_board, name='Future history challenge', startsOn=(today + timedelta(days=7)).isoformat(),
                    endsOn=(today + timedelta(days=14)).isoformat())
status, _, headers = member.post('/leaderboards/new', '/leaderboards/save', future_board)
future_text = anon.request(headers['Location'])[1]
check(status == 302 and 'Weekly rankings will appear once it begins.' in future_text and 'rel="prev"' not in future_text,
      'future leaderboard shows its start notice without week navigation')
check(anon.request('/strava/sync', {})[0] == 302, 'anonymous sync requests rejected')
check(member.request('/strava/sync', {})[0] == 400, 'manual sync requires antiforgery token')
status, _, headers = member.post('/strava', '/strava/sync', {})
check(status == 302 and headers['Location'] == '/strava?reconnect=true', 'unconnected account cannot enqueue a sync')
check(anon.request('/webhooks/strava?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=test')[0] == 403,
      'webhook verification rejects wrong token')
status, text, _ = anon.request('/webhooks/strava?hub.mode=subscribe&hub.verify_token=ci-only-webhook-verification-token-335&hub.challenge=test-335')
check(status == 200 and json.loads(text) == {'hub.challenge': 'test-335'}, 'webhook verification echoes the Strava challenge')
def webhook(subscription_id):
    request = urllib.request.Request(BASE + '/webhooks/strava', data=json.dumps({
        'subscription_id': subscription_id, 'owner_id': 999999, 'object_id': 501,
        'object_type': 'activity', 'aspect_type': 'create', 'updates': {}}).encode(),
        headers={'Content-Type': 'application/json'})
    try:
        response = urllib.request.urlopen(request, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status
check(webhook(999) == 403, 'webhooks reject a different subscription')
check(webhook(123) == 200, 'webhooks safely acknowledge unknown athletes without importing data')
print('All integration checks passed.')
