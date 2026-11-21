import os
import tempfile
import unittest
from unittest.mock import patch

import settings
from services.player_hub import PlayerHub
from tests.support import FakeHathorServer


class _FakeAiDj:
    def view(self):
        return {'active': False}


class _FakePlayback:
    def __init__(self):
        self.track_listeners = []
        self.queue_listeners = []
        self.exhausted_listeners = []
        self.next_songs = []
        self.position = 0.0
        self.populated = None
        self.played = []
        self.paused_toggles = 0
        self.seeks = []

    def add_track_changed_listener(self, callback):
        self.track_listeners.append(callback)

    def add_queue_changed_listener(self, callback):
        self.queue_listeners.append(callback)

    def add_queue_exhausted_listener(self, callback):
        self.exhausted_listeners.append(callback)

    def get_current_pos(self):
        return self.position

    def _read_source(self):
        return {'type': 'all_songs', 'id': None}

    def populate_queue_from_list(self, current, songs, playlist_id=None, source=None):
        self.populated = (current, songs, playlist_id, source)

    def play_button(self, current_song=None, opening=False):
        self.played.append(current_song)
        return True


class _FakeMetadata:
    def get_song_metadata(self, path, file, include_cover=False):
        return {'File': file, 'Title': file, 'Artist': 'Artist', 'Duration': 100}


class _FakeApi:
    def __init__(self, db_path, root):
        self.db_path = db_path
        self.playback = _FakePlayback()
        self.current_filename = 'a.mp3'
        self.last_song = {'File': 'a.mp3', 'Duration': 100, 'IsPodcast': False}
        self.playing = True
        self.ai_dj = _FakeAiDj()
        self.metadata = _FakeMetadata()
        self._window = _FakeWindow()
        self.progress_calls = []
        self.volume_calls = []

    def progress_slider_click(self, seconds, publish=True):
        self.progress_calls.append(seconds)
        self.playback.seeks.append(seconds)

    def volume_slider(self, volume, publish=True):
        self.volume_calls.append(volume)
        settings.volume = volume


class _FakeWindow:
    def __init__(self):
        self.scripts = []

    def evaluate_js(self, code):
        self.scripts.append(code)


class _FakeSync:
    def __init__(self, state=None):
        self.state_value = state or {}
        self.calls = []

    def state(self):
        self.calls.append(('state',))
        return self.state_value

    def replace_queue(self, current_file, files, session_id=None, source=None):
        self.calls.append(('queue', current_file, files, source))
        return {'queueVersion': 4, 'currentSong': {'File': current_file}}

    def play(self, file, is_podcast=False):
        self.calls.append(('play', file, is_podcast))
        return {
            'currentSong': {'File': file},
            'isPlaying': True,
            'volume': settings.volume,
            'positionSec': 0,
            'queueVersion': 5,
        }

    def pause(self, file=None, position=None):
        self.calls.append(('pause', file, position))
        return {
            'currentSong': {'File': file},
            'isPlaying': False,
            'volume': settings.volume,
            'positionSec': position or 0,
            'queueVersion': 5,
        }

    def resume(self):
        self.calls.append(('resume',))
        return {
            'currentSong': {'File': 'a.mp3'},
            'isPlaying': True,
            'volume': settings.volume,
            'positionSec': 10,
            'queueVersion': 5,
        }

    def seek(self, seconds):
        self.calls.append(('seek', seconds))
        return {
            'currentSong': {'File': 'a.mp3'},
            'isPlaying': True,
            'volume': settings.volume,
            'positionSec': seconds,
            'queueVersion': 5,
        }

    def volume(self, volume):
        self.calls.append(('volume', volume))
        return {
            'currentSong': {'File': 'a.mp3'},
            'isPlaying': True,
            'volume': volume,
            'positionSec': 10,
            'queueVersion': 5,
        }


class PlayerHubTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.api = _FakeApi(os.path.join(self.tmp.name, 'library.db'), self.tmp.name)
        self.sync = _FakeSync()
        self.hub = PlayerHub(self.api, start_poll=False, sync=self.sync)
        self.config = patch('services.player_hub.get_api_config',
                            return_value=('http://host:5051', 'hth_secret'))
        self.config.start()
        self.volume = patch.object(settings, 'volume', 0.8)
        self.volume.start()

    def tearDown(self):
        self.volume.stop()
        self.config.stop()
        self.tmp.cleanup()

    def test_publishes_queue_play_transport_seek_and_volume(self):
        self.api.playback.next_songs = [{'File': 'b.mp3'}]
        self.hub._publish_queue()
        self.hub._publish_track({'File': 'a.mp3', 'IsPodcast': False})

        self.assertEqual(self.sync.calls[0][0], 'queue')
        self.assertEqual(self.sync.calls[0][2], ['a.mp3', 'b.mp3'])
        self.assertTrue(any(call[0] == 'play' for call in self.sync.calls))

        self.api.playing = False
        self.hub.publish_transport()
        self.assertIn(('pause', 'a.mp3', 0.0), self.sync.calls)
        self.hub.publish_seek(42)
        self.assertIn(('seek', 42), self.sync.calls)
        self.hub.publish_volume(0.45)
        self.assertIn(('volume', 0.45), self.sync.calls)

    def test_remote_pause_pauses_local_playback(self):
        self.sync.state_value = {
            'currentSong': {'File': 'a.mp3'},
            'isPlaying': False,
            'volume': 0.8,
            'positionSec': 18,
            'queueVersion': 2,
        }
        self.hub.poll_once()
        self.assertEqual(self.api.playback.played, [None])  # toggle pause
        self.assertFalse(any(call[0] == 'resume' for call in self.sync.calls))

    def test_remote_next_loads_matching_local_queue(self):
        open(os.path.join(self.tmp.name, 'b.mp3'), 'wb').close()
        with patch('services.player_hub.settings.path', self.tmp.name), \
                patch('services.player_hub.settings.podcasts_path',
                      os.path.join(self.tmp.name, 'podcasts')):
            self.sync.state_value = {
                'currentSong': {'File': 'b.mp3', 'IsPodcast': False},
                'isPlaying': True,
                'volume': 0.8,
                'positionSec': 12,
                'queueVersion': 9,
                'queue': [],
                'source': {'type': 'album', 'id': 'Album'},
            }
            self.hub.poll_once()

        self.assertIsNotNone(self.api.playback.populated)
        current, songs, _, source = self.api.playback.populated
        self.assertEqual(current['File'], 'b.mp3')
        self.assertEqual(songs[0]['File'], 'b.mp3')
        self.assertEqual(source, {'type': 'album', 'id': 'Album'})
        self.assertEqual(self.api.playback.played[0]['File'], 'b.mp3')

    def test_missing_remote_track_keeps_local_audio_and_notifies(self):
        self.sync.state_value = {
            'currentSong': {'File': 'missing.mp3', 'IsPodcast': False},
            'isPlaying': True,
            'volume': 0.8,
            'positionSec': 0,
            'queueVersion': 9,
            'queue': [],
        }
        with patch('services.player_hub.settings.path', self.tmp.name), \
                patch('services.player_hub.settings.podcasts_path',
                      os.path.join(self.tmp.name, 'podcasts')):
            self.hub.poll_once()

        self.assertIsNone(self.api.playback.populated)
        self.assertTrue(any('showPlayerHubNotice' in script
                            for script in self.api._window.scripts))
        self.assertEqual(self.api.current_filename, 'a.mp3')

    def test_fake_server_poll_applies_remote_pause(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/player/state", json_body={
                'currentSong': {'File': 'a.mp3'},
                'isPlaying': False,
                'volume': 0.8,
                'positionSec': 22,
                'queueVersion': 4,
                'queue': [],
            })
            with patch('services.player_hub.get_api_config',
                       return_value=(server.base_url, 'hth_secret')), \
                    patch('services.player_sync.get_api_config',
                          return_value=(server.base_url, 'hth_secret')):
                hub = PlayerHub(self.api, start_poll=False)
                hub.poll_once()

            self.assertEqual(server.requests[0]['route'], '/api/v1/player/state')
            self.assertEqual(self.api.playback.played, [None])

    def test_own_echo_position_drift_is_ignored(self):
        self.hub._remember({
            'currentSong': {'File': 'a.mp3'},
            'isPlaying': True,
            'volume': 0.8,
            'positionSec': 10,
            'queueVersion': 3,
        })
        self.sync.state_value = {
            'currentSong': {'File': 'a.mp3'},
            'isPlaying': True,
            'volume': 0.8,
            'positionSec': 13,
            'queueVersion': 3,
        }
        self.hub.poll_once()
        self.assertEqual(self.api.progress_calls, [])

    def test_remote_seek_applies_when_projection_drifts(self):
        self.hub._remember({
            'currentSong': {'File': 'a.mp3'},
            'isPlaying': True,
            'volume': 0.8,
            'positionSec': 10,
            'queueVersion': 3,
        })
        self.sync.state_value = {
            'currentSong': {'File': 'a.mp3'},
            'isPlaying': True,
            'volume': 0.8,
            'positionSec': 80,
            'queueVersion': 3,
        }
        self.hub.poll_once()
        self.assertEqual(self.api.progress_calls, [80])


if __name__ == '__main__':
    unittest.main()

