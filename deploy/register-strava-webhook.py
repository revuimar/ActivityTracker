#!/usr/bin/env python3
"""Run on the VPS after deployment; credentials stay in memory, never in arguments/output."""
import json
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


def main():
    ids = subprocess.check_output([
        'docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=activitytracker',
        '--filter', 'label=com.docker.compose.service=app'], text=True).split()
    if len(ids) != 1:
        sys.exit('Expected exactly one running Activity Tracker app container.')
    raw = subprocess.check_output(['docker', 'inspect', ids[0], '--format', '{{json .Config.Env}}'], text=True)
    env = dict(item.split('=', 1) for item in json.loads(raw))
    client_id = env.get('Strava__ClientId', '')
    client_secret = env.get('Strava__ClientSecret', '')
    token = env.get('Strava__WebhookVerifyToken', '')
    redirect = urllib.parse.urlsplit(env.get('Strava__RedirectUri', ''))
    if not client_id.isdigit() or not client_secret or len(token) < 32:
        sys.exit('Set STRAVA_CLIENT_ID, STRAVA_CLIENT_SECRET and STRAVA_WEBHOOK_VERIFY_TOKEN (32+ characters), then recreate the app.')
    if redirect.scheme != 'https' or not redirect.hostname or redirect.username or redirect.password:
        sys.exit('Set a valid HTTPS STRAVA_REDIRECT_URI, then recreate the app.')
    callback = urllib.parse.urlunsplit(('https', redirect.netloc, '/webhooks/strava', '', ''))
    endpoint = 'https://www.strava.com/api/v3/push_subscriptions'
    credentials = {'client_id': client_id, 'client_secret': client_secret}
    try:
        # Strava documents credentials in the query for this GET. Never print its URL/errors.
        with urllib.request.urlopen(endpoint + '?' + urllib.parse.urlencode(credentials), timeout=20) as response:
            subscriptions = json.load(response)
        if subscriptions:
            matching = [s for s in subscriptions if s.get('callback_url') == callback]
            if not matching:
                sys.exit('A different webhook is already registered. Review it in Strava before replacing it; nothing was changed.')
            subscription_id = matching[0]['id']
            print('Existing webhook found. If verification fails, check the token used when it was created.')
        else:
            body = urllib.parse.urlencode(dict(credentials, callback_url=callback, verify_token=token)).encode()
            with urllib.request.urlopen(endpoint, data=body, timeout=20) as response:
                subscription_id = json.load(response)['id']
    except urllib.error.HTTPError as error:
        sys.exit(f'Strava returned HTTP {error.code}. Check credentials and public webhook verification; no credentials were printed.')
    except (urllib.error.URLError, TimeoutError, ValueError, KeyError):
        sys.exit('Webhook registration could not be verified. Check connectivity/configuration and retry; no credentials were printed.')
    print(f'Put this in /opt/activitytracker/shared/.env: STRAVA_WEBHOOK_SUBSCRIPTION_ID={subscription_id}')
    print('Recreate the app once more to enable event processing.')


if __name__ == '__main__':
    main()
