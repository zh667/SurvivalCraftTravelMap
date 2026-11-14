"""Run the travel map lab in an isolated copy of the Survivalcraft API runtime and wait for its result file.

python run_isolated.py --mode season|touch [--source 1.9.3.1|1.9.5.1|net] [--package <scmod|netmod>] [--label <name>] [--extra <mod> ...]

--source net runs the multiplayer edition (SurvivalcraftNet 2.4, x26.07.01.01) as a local game, in one fixed folder
(runtime-net) that is wiped every run: Windows asks about its firewall once per program path. Its window is hidden
and the cursor released every 0.2 s (the approach of E:/projects/SurvivalcraftRuins-test).

The runtime lives in runtime-<mode>-<source>[-label] next to this script; the user's game install, mods, worlds and settings
are never touched (only binaries and Content.zip are copied). The lab mod is built from TravelMapLab/ against the travel map
DLL inside the package under test.
"""
import argparse, ctypes, glob, json, os, re, shutil, subprocess, sys, time, zipfile
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--mode', choices=['season', 'touch'], required=True)
    ap.add_argument('--source', choices=SOURCES, default='1.9.3.1')
    ap.add_argument('--package')
    ap.add_argument('--label', default='')
    ap.add_argument('--extra', nargs='*', default=[])
    ap.add_argument('--limit', type=int, default=900)
    a = ap.parse_args()
    net = a.source == 'net'
    current = ROOT / 'pkg' / ('SurvivalcraftTravelMap.netmod' if net else 'SurvivalcraftTravelMap.scmod')
    source, package = SOURCES[a.source], Path(a.package) if a.package else current
    if net:
        runtime = ROOT / 'runtime-net'
        if runtime.exists():
            shutil.rmtree(runtime)   # fresh worlds and settings every run
    else:
        runtime = ROOT / f"runtime-{a.mode}-{a.source}{'-' + a.label if a.label else ''}"
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
    worlds = runtime / 'doc' / 'Worlds'
    assert ROOT in worlds.parents
    if worlds.exists() and not net: shutil.rmtree(worlds)
    res = runtime / 'lab-result.json'
    if res.exists(): res.unlink()
    shots = runtime / 'ScreenCapture'
    if shots.exists(): shutil.rmtree(shots)

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
        free_cursor()
    print('elapsed', round(time.time() - t0), 'exit', proc.poll())
    print(res.read_text(encoding='utf-8')[-8000:] if res.exists() else 'NO RESULT')
    shot_files = sorted(shots.glob('*.png')) if shots.exists() else []
    print(len(shot_files), 'screenshots:', ', '.join(f.name for f in shot_files))
    for l in sorted(glob.glob(str(runtime / '*.log')) + glob.glob(str(runtime / 'Bugs/*')) + glob.glob(str(runtime / '**/*.log'), recursive=True), key=os.path.getmtime)[-2:]:
        text = Path(l).read_text(encoding='utf-8', errors='replace')
        print('LOG', l)
        print('\n'.join(line for line in text.splitlines() if 'TravelMap' in line or 'Exception' in line or 'rror' in line)[-4000:])
    try:
        passed = json.loads(res.read_text(encoding='utf-8')).get('passed') is True if res.exists() else False
    except ValueError:
        passed = False
    if not passed:
        sys.exit(f'{a.mode} {a.source}: FAILED')


if __name__ == '__main__':
    main()
