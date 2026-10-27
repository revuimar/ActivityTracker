"""End-to-end authorization, membership, local login and antiforgery checks; stdlib only."""
import html
import http.cookiejar
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
check(anon.request('/leaderboards/1/members', {})[0] == 302, 'anonymous membership changes rejected')
register(admin, 'admin@example.test', 'Alice Admin')
register(member, 'member@example.test', 'Bob Member')
status, text, _ = anon.request('/leaderboards/1')
check(status == 200 and 'Alice Admin' in text and 'Bob Member' in text, 'all accounts appear publicly without workouts')
check('admin@example.test' not in text and 'member@example.test' not in text, 'emails stay private')
check(member.request('/leaderboards/1/members')[0] == 403, 'membership screen restricted to administrator')
check(member.request('/leaderboards/1/members', {'participantId': '2', 'action': 'exclude'})[0] == 302,
      'non-admin membership POST rejected')
check(member.request('/leaderboards/save', {'id': '1', 'name': 'CSRF attack'})[0] == 400,
      'edits require valid antiforgery token')
check(admin.request('/leaderboards/1/members', {'participantId': '3', 'action': 'exclude'})[0] == 400,
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
    status, _, _ = admin.post('/leaderboards/1/members', '/leaderboards/1/members',
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
status, _, _ = admin.post('/leaderboards/1/members', '/leaderboards/1/members',
                          {'participantId': '3', 'action': 'include'})
check(status == 302 and 'Bob Member' in anon.request('/leaderboards/1')[1], 'administrator restores participant')
invalid_board = dict(board, endsOn='2019-01-01')
check(member.post(board_url + '/edit', '/leaderboards/save', invalid_board)[0] == 400, 'invalid date range rejected')
check(anon.request('/leaderboards/999999')[0] == 404, 'missing leaderboard returns 404')
print('All integration checks passed.')
