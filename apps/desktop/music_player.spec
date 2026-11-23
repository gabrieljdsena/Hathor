# -*- mode: python ; coding: utf-8 -*-
import os
from PyInstaller.utils.hooks import collect_data_files

block_cipher = None

# FFmpeg is auto-downloaded at startup when missing, so the build must not
# fail if ./ffmpeg was never placed locally (it is gitignored).
_extra_datas = []
if os.path.isdir('ffmpeg'):
    _extra_datas.append(('ffmpeg', 'ffmpeg'))

a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=[],
    datas=[
        ('ui', 'ui'),
        ('database.sql', '.'),
        ('version.json', '.')
    ] + _extra_datas + collect_data_files('pykakasi'),
    hiddenimports=[
        'webview',
        'pygame',
        'yt_dlp',
        'mutagen',
        'dotenv',
        # services/__init__.py resolves these lazily; PyInstaller cannot see
        # string-based import_module calls.
        'services.playback',
        'services.metadata',
        'services.database',
        'services.windows_media',
        'services.lyrics',
        'services.apple',
        'services.downloads',
        'services.discover',
        'services.ai_dj',
        'services.app_update',
        'services.startup_maintenance',
        'services.api_sync',
        'services.api_config',
        'services.hathor_api',
        'services.history_sync',
        'services.player_hub',
        'services.telemetry',
        'services.images',
        'services.library_moves',
        'services.loudness',
        'services.player_sync',
        'services.platform_support',
        'services.window_chrome',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)
pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='Hathor',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='Hathor',
)
