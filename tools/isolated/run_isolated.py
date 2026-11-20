"""Run the travel map lab in an isolated copy of the Survivalcraft API runtime and wait for its result file.

python run_isolated.py --mode season|touch|markers|shading|upgrade-old|upgrade-new [--source 1.9.3.1|1.9.5.1|net]
                       [--package <scmod|netmod>] [--label <name>] [--extra <mod> ...]

shading: relief light direction on the large map, in the map data and in the screenshot pixels (checked here).
upgrade-old / upgrade-new: run upgrade-old (plugin edition: with an older package, --package 2.8.0; multiplayer edition:
with the current package, the lab cannot load next to an older build there), then upgrade-new with the current one; both
use the same runtime, and upgrade-new reopens the world upgrade-old saved. upgrade-new also checks that the map cache moved
from the old directory-name key to the path key (the multiplayer run moves its cache to the old key first).
attack: a creature hits the player in a challenging world (the Ghoul mod's corpse head with --extra <ghoul>, else a wolf).
Every run lists %LOCALAPPDATA%/Survivalcraft/SurvivalcraftTravelMap (read-only) before and after and fails on any change:
the lab redirects data:/ into the runtime (isolated-data/) on both editions.
Screenshots of every run are kept in shots/<runtime>-<mode>/ next to this script.

--source net runs the multiplayer edition (SurvivalcraftNet 2.4, x26.07.01.01) as a local game, in one fixed folder
(runtime-net) that is wiped every run: Windows asks about its firewall once per program path. Its window is hidden
and the cursor released every 0.2 s (the approach of E:/projects/SurvivalcraftRuins-test).

The runtime lives in runtime-<mode>-<source>[-label] next to this script; the user's game install, mods, worlds and settings
are never touched (only binaries and Content.zip are copied). The lab mod is built from TravelMapLab/ against the travel map
DLL inside the package under test.
"""
import argparse, ctypes, glob, json, os, re, shutil, struct, subprocess, sys, time, zipfile, zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SOURCES = {'1.9.3.1': Path(r'D:\下载\[Windows]SurvivalcraftAPI_1.9.3.1'),
           '1.9.5.1': Path(r'D:\下载\[Windows]SurvivalcraftAPI_1.9.5.1'),
           'net': Path(r'D:\下载\x26.07.01.01')}
SKIP_FILES = {'settings.xml', 'modsettings.xml', 'communitycontentcache.xml', 'gpssetting.xml'}


def hide_windows(pid):
    """The multiplayer runtime shows its window whatever STARTUPINFO says; hide every visible window of the process."""
    from ctypes import wintypes
    user32 = ctypes.WinDLL('user32', use_last_error=True)
    enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = [enum_proc, wintypes.LPARAM]
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
    found = []

    def visit(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True
    callback = enum_proc(visit)
    user32.EnumWindows(callback, 0)
    for hwnd in found:
        user32.ShowWindow(hwnd, 0)
    return len(found)


def free_cursor():
    try:
        ctypes.windll.user32.ClipCursor(None)
    except Exception:
        pass
NO_WINDOW = 0x08000000


def read_png(path):
    """Minimal PNG reader (8-bit RGB/RGBA, not interlaced): returns width, height, rows of RGB tuples."""
    data = Path(path).read_bytes()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', 'not a png'
    pos, idat, width = 8, b'', 0
    while pos < len(data):
        length, kind = struct.unpack('>I4s', data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b'IHDR':
            width, height, depth, color, _, _, interlace = struct.unpack('>IIBBBBB', body)
            assert depth == 8 and color in (2, 6) and interlace == 0, f'unsupported png {depth} {color} {interlace}'
            channels = 4 if color == 6 else 3
        elif kind == b'IDAT':
            idat += body
        pos += 12 + length
    raw = zlib.decompress(idat)
    stride = width * channels
    rows, previous, i = [], bytearray(stride), 0
    for _ in range(height):
        f, line = raw[i], bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - channels] if x >= channels else 0
            b = previous[x]
            c = previous[x - channels] if x >= channels else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + ((a + b) >> 1)) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append([tuple(line[x:x + 3]) for x in range(0, stride, channels)])
        previous = line
    return width, height, rows


def read_image(path):
    """Screenshots: the plugin edition writes PNG, the multiplayer edition JPEG under a .png name."""
    if Path(path).read_bytes()[:8] == b'\x89PNG\r\n\x1a\n':
        return read_png(path)
    from PIL import Image   # JPEG needs a real decoder
    with Image.open(path) as im:
        im = im.convert('RGB')
        width, height = im.size
        data = list(im.getdata())
    return width, height, [data[y * width:(y + 1) * width] for y in range(height)]


def write_png(path, rows):
    height, width = len(rows), len(rows[0])
    raw = b''.join(b'\x00' + bytes(v for px in row for v in px) for row in rows)
    def chunk(kind, body): return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body) & 0xffffffff)
    Path(path).write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
                           + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def check_pixels(result, shots_dir, keep_dir):
    """The lab names, per screenshot, the screen pixel of each sampled cell; compare their brightness here."""
    failures = []
    for check in result.get('metrics', {}).get('pixelChecks', []):
        path = shots_dir / check['shot']
        if not path.exists():
            failures.append(f"{check['shot']}: screenshot missing"); continue
        width, height, rows = read_image(path)
        print(f"PIXELS {check['label']}: {check['shot']} {width}x{height} (capture {check['width']}x{check['height']})")
        def lum(p):
            xs = [min(max(p['x'] + dx, 0), width - 1) for dx in (-1, 0, 1)]
            ys = [min(max(p['y'] + dy, 0), height - 1) for dy in (-1, 0, 1)]
            px = [rows[y][x] for y in ys for x in xs]
            return sum(0.299 * r + 0.587 * g + 0.114 * b for r, g, b in px) / len(px), px[4]
        all_points = []
        for site, sides in check['sides'].items():
            l = {k: lum(v) for k, v in sides.items()}
            for k, v in sides.items():
                expected = [min(255, round(c * v['shade'] / 128)) for c in v['color']]
                print(f"  {site:8} {k:6} cell {v['cell']:10} screen ({v['x']},{v['y']}) shade {v['shade']:3} "
                      f"map colour x shade {expected} screenshot {list(l[k][1])} luminance {l[k][0]:.1f}")
                all_points.append((v['x'], v['y']))
            brighter = (site == 'platform') == check['topLeftLight']
            ok = (l['left'][0] > l['right'][0] and l['top'][0] > l['bottom'][0]) if brighter \
                else (l['left'][0] < l['right'][0] and l['top'][0] < l['bottom'][0])
            verdict = 'OK' if ok else 'FAILED'
            print(f"  {site}: screen-left {l['left'][0]:.1f} vs right {l['right'][0]:.1f}, top {l['top'][0]:.1f} vs bottom {l['bottom'][0]:.1f}"
                  f" -> expect top-left {'brighter' if brighter else 'darker'}: {verdict}")
            if not ok:
                failures.append(f"{check['label']} {site} pixels")
        # A 4x enlarged crop around the two sites for people to look at.
        x0 = max(0, min(x for x, _ in all_points) - 30); x1 = min(width, max(x for x, _ in all_points) + 30)
        y0 = max(0, min(y for _, y in all_points) - 30); y1 = min(height, max(y for _, y in all_points) + 30)
        crop = [[px for px in rows[y][x0:x1] for _ in range(4)] for y in range(y0, y1) for _ in range(4)]
        write_png(keep_dir / (Path(check['shot']).stem + '_crop4x.png'), crop)
    return failures


USER_DATA = Path(os.environ.get('LOCALAPPDATA', '')) / 'Survivalcraft' / 'SurvivalcraftTravelMap'


def snapshot_user_data():
    """Read-only listing (relative path -> size, mtime) of the travel map's per-user folder the real games use."""
    result = {}
    if USER_DATA.is_dir():
        for root, _, files in os.walk(USER_DATA):
            for name in files:
                p = Path(root) / name
                try:
                    st = p.stat()
                    result[str(p.relative_to(USER_DATA))] = (st.st_size, st.st_mtime_ns)
                except OSError:
                    pass
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--mode', choices=['season', 'touch', 'markers', 'shading', 'upgrade-old', 'upgrade-new', 'attack'], required=True)
    ap.add_argument('--source', choices=SOURCES, default='1.9.3.1')
    ap.add_argument('--package')
    ap.add_argument('--label', default='')
    ap.add_argument('--extra', nargs='*', default=[])
    ap.add_argument('--limit', type=int, default=900)
    a = ap.parse_args()
    net = a.source == 'net'
    current = ROOT / 'pkg' / ('SurvivalcraftTravelMap.netmod' if net else 'SurvivalcraftTravelMap.scmod')
    source, package = SOURCES[a.source], Path(a.package) if a.package else current
    upgrade = a.mode.startswith('upgrade')
    if net:
        runtime = ROOT / 'runtime-net'
        if runtime.exists() and a.mode != 'upgrade-new':
            shutil.rmtree(runtime)   # fresh worlds and settings every run (upgrade-new reopens upgrade-old's world)
    else:
        kind = 'upgrade' if upgrade else a.mode
        runtime = ROOT / f"runtime-{kind}-{a.source}{'-' + a.label if a.label else ''}"
    mods = runtime / ('NetMods' if net else 'Mods')
    mods.mkdir(parents=True, exist_ok=True)
    for f in source.iterdir():
        if f.is_file() and f.suffix.lower() in ('.dll', '.exe', '.json', '.zip', '.js', '.xml', '.scpak') \
                and not (net and f.name.lower() in SKIP_FILES):
            shutil.copy2(f, runtime / f.name)
    for d in ('runtimes', 'OpenAL'):
        if (source / d).exists():
            shutil.copytree(source / d, runtime / d, dirs_exist_ok=True)
    if net and (source / 'Settings.xml').exists():   # the user's settings, muted
        text = (source / 'Settings.xml').read_text(encoding='utf-8-sig')
        text = re.sub(r'(Name="(?:SoundsVolume|MusicVolume)"[^>]*?Value=")[^"]*"', r'\g<1>0"', text)
        (runtime / 'Settings.xml').write_text(text, encoding='utf-8')
    if net:   # Harmony and its MonoMod dependencies for the lab (the multiplayer edition ships none)
        deps = runtime / 'labdeps'
        deps.mkdir(exist_ok=True)
        for f in SOURCES['1.9.3.1'].iterdir():
            if f.suffix.lower() == '.dll' and (f.name == '0Harmony.dll' or f.name.startswith(('MonoMod.', 'Mono.Cecil'))):
                shutil.copy2(f, deps / f.name)
    print('runtime', runtime, 'from', source)

    # Build the lab against the DLL inside the package under test.
    build = ROOT / f'build-{runtime.name}'
    if build.exists(): shutil.rmtree(build)
    build.mkdir()
    with zipfile.ZipFile(package) as z:
        info = json.loads(z.read('modinfo.json').decode('utf-8-sig'))
    # The lab always compiles against the current package; an older package under test (the negative control) only
    # runs the touch mode, which touches nothing that 2.7.x lacks.
    with zipfile.ZipFile(current) as z:
        (build / 'SurvivalcraftTravelMap.dll').write_bytes(z.read('SurvivalcraftTravelMap.dll'))
    print('package', package, 'version', info['Version'])
    p = subprocess.run(['dotnet', 'build', str(ROOT / 'TravelMapLab' / 'TravelMapLab.csproj'), '-c', 'Release', '--nologo', '-v', 'q',
                        f'-p:TravelMapDll={build / "SurvivalcraftTravelMap.dll"}',
                        f'-p:ScInstall={SOURCES["net" if net else "1.9.3.1"]}', f'-p:TmFlavor={"net" if net else "scapi"}',
                        f'-p:OutDir={build / "out"}\\', f'-p:BaseIntermediateOutputPath={build / "obj"}\\'],
                       capture_output=True, text=True, encoding='utf-8', errors='replace', creationflags=NO_WINDOW)
    if p.returncode:
        print(p.stdout[-4000:]); sys.exit('lab build failed')

    for f in mods.iterdir():
        f.unlink()
    shutil.copy2(package, mods / package.name)
    for extra in a.extra:
        shutil.copy2(extra, mods / Path(extra).name)
        print('extra mod', extra)
    if net:   # Net 2.4 reads only its own modinfo fields, Dependencies as a list
        lab_info = dict(Name='TravelMapLab', Version='1.0.0', ApiVersion='1.44', Description='Isolated tests only',
                        ScVersion='2.4.40.6', Link='', Author='Tests', PackageName='local.travelmaplab', Dependencies=[])
    else:
        lab_info = dict(Name='TravelMapLab', Version='1.0.0', ApiVersion='1.9.2.1', ScVersion='2.4', PackageName='local.travelmaplab',
                        Author='Tests', Description='Isolated tests only', LoadOrder=1300,
                        Dependencies={info['PackageName']: info['Version']})
    with zipfile.ZipFile(mods / ('TravelMapLab.netmod' if net else 'TravelMapLab.scmod'), 'w') as z:
        z.write(build / 'out' / 'TravelMapLab.dll', 'TravelMapLab.dll')
        z.writestr('modinfo.json', json.dumps(lab_info))
    (runtime / 'lab-mode.txt').write_text(a.mode, encoding='ascii')
    (runtime / 'lab-package-version.txt').write_text(info['Version'], encoding='ascii')
    worlds = runtime / 'doc' / 'Worlds'
    assert ROOT in worlds.parents
    if worlds.exists() and not net and a.mode != 'upgrade-new': shutil.rmtree(worlds)
    if a.mode == 'upgrade-old' and (runtime / 'lab-site.json').exists(): (runtime / 'lab-site.json').unlink()
    # The lab redirects data:/ here on the plugin edition (see IsolatedDataDirectory in Lab.cs).
    isolated_data = runtime / 'isolated-data'
    if isolated_data.exists() and a.mode != 'upgrade-new': shutil.rmtree(isolated_data)
    res = runtime / 'lab-result.json'
    if res.exists(): res.unlink()
    shots = runtime / 'ScreenCapture'
    if shots.exists(): shutil.rmtree(shots)

    user_before = snapshot_user_data()
    si = subprocess.STARTUPINFO(); si.dwFlags |= subprocess.STARTF_USESHOWWINDOW; si.wShowWindow = 0   # hidden window
    proc = subprocess.Popen([str(runtime / 'Survivalcraft.exe')], cwd=runtime, startupinfo=si)
    t0 = time.time()
    try:
        while time.time() - t0 < a.limit and not res.exists() and proc.poll() is None:
            if net:
                hide_windows(proc.pid)
            free_cursor()
            time.sleep(0.2)
        for _ in range(15):
            free_cursor(); time.sleep(0.2)
    finally:
        if proc.poll() is None: proc.kill()
        try:
            proc.wait(15)   # let the killed game release its log file
        except subprocess.TimeoutExpired:
            pass
        free_cursor()
    print('elapsed', round(time.time() - t0), 'exit', proc.poll())
    print(res.read_text(encoding='utf-8')[-8000:] if res.exists() else 'NO RESULT')
    shot_files = sorted(shots.glob('*.png')) if shots.exists() else []
    print(len(shot_files), 'screenshots:', ', '.join(f.name for f in shot_files))
    keep = ROOT / 'shots' / f'{runtime.name}-{a.mode}'
    if keep.exists(): shutil.rmtree(keep)
    keep.mkdir(parents=True)
    for f in shot_files: shutil.copy2(f, keep / f.name)
    pixel_failures = []
    if res.exists():
        try:
            pixel_failures = check_pixels(json.loads(res.read_text(encoding='utf-8')), shots, keep)
        except Exception as e:
            pixel_failures = [f'pixel check error {e!r}']
    print('screenshots kept in', keep)
    for l in sorted(glob.glob(str(runtime / '*.log')) + glob.glob(str(runtime / 'Bugs/*')) + glob.glob(str(runtime / '**/*.log'), recursive=True), key=os.path.getmtime)[-2:]:
        try:
            text = Path(l).read_text(encoding='utf-8', errors='replace')
        except OSError as e:
            print('LOG', l, 'unreadable:', e); continue
        print('LOG', l)
        print('\n'.join(line for line in text.splitlines() if 'TravelMap' in line or 'Exception' in line or 'rror' in line)[-4000:])
    try:
        passed = json.loads(res.read_text(encoding='utf-8')).get('passed') is True if res.exists() else False
    except ValueError:
        passed = False
    if pixel_failures:
        print('PIXEL CHECK FAILED:', '; '.join(pixel_failures))
    user_after = snapshot_user_data()
    user_changes = sorted(set(user_before.items()) ^ set(user_after.items()))
    if user_changes:
        print('USER DATA CHANGED:', [k for k, _ in user_changes][:20])
    else:
        print('user data untouched:', len(user_after), 'files under', USER_DATA)
    if not passed or pixel_failures or user_changes:
        sys.exit(f'{a.mode} {a.source}: FAILED')


if __name__ == '__main__':
    main()
