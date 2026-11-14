"""Run the travel map lab in an isolated copy of the Survivalcraft API runtime and wait for its result file.

python run_isolated.py --mode season|touch [--source 1.9.3.1|1.9.5.1] [--package <scmod>] [--label <name>] [--extra <scmod> ...]

The runtime lives in runtime-<mode>-<source>[-label] next to this script; the user's game install, mods, worlds and settings
are never touched (only binaries and Content.zip are copied). The lab mod is built from TravelMapLab/ against the travel map
DLL inside the package under test.
"""
import argparse, glob, json, os, shutil, subprocess, sys, time, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SOURCES = {'1.9.3.1': Path(r'D:\下载\[Windows]SurvivalcraftAPI_1.9.3.1'),
           '1.9.5.1': Path(r'D:\下载\[Windows]SurvivalcraftAPI_1.9.5.1')}
NO_WINDOW = 0x08000000


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--mode', choices=['season', 'touch'], required=True)
    ap.add_argument('--source', choices=SOURCES, default='1.9.3.1')
    ap.add_argument('--package', default=str(ROOT / 'pkg' / 'SurvivalcraftTravelMap.scmod'))
    ap.add_argument('--label', default='')
    ap.add_argument('--extra', nargs='*', default=[])
    ap.add_argument('--limit', type=int, default=900)
    a = ap.parse_args()
    source, package = SOURCES[a.source], Path(a.package)
    runtime = ROOT / f"runtime-{a.mode}-{a.source}{'-' + a.label if a.label else ''}"
    mods = runtime / 'Mods'
    mods.mkdir(parents=True, exist_ok=True)
    for f in source.iterdir():
        if f.is_file() and f.suffix.lower() in ('.dll', '.exe', '.json', '.zip', '.js', '.xml'):
            shutil.copy2(f, runtime / f.name)
    if (source / 'runtimes').exists():
        shutil.copytree(source / 'runtimes', runtime / 'runtimes', dirs_exist_ok=True)
    print('runtime', runtime, 'from', source)

    # Build the lab against the DLL inside the package under test.
    build = ROOT / f'build-{runtime.name}'
    if build.exists(): shutil.rmtree(build)
    build.mkdir()
    with zipfile.ZipFile(package) as z:
        info = json.loads(z.read('modinfo.json').decode('utf-8-sig'))
        (build / 'SurvivalcraftTravelMap.dll').write_bytes(z.read('SurvivalcraftTravelMap.dll'))
    print('package', package, 'version', info['Version'])
    p = subprocess.run(['dotnet', 'build', str(ROOT / 'TravelMapLab' / 'TravelMapLab.csproj'), '-c', 'Release', '--nologo', '-v', 'q',
                        f'-p:TravelMapDll={build / "SurvivalcraftTravelMap.dll"}', f'-p:ScInstall={SOURCES["1.9.3.1"]}',
                        f'-p:OutDir={build / "out"}\\', f'-p:BaseIntermediateOutputPath={build / "obj"}\\'],
                       capture_output=True, text=True, encoding='utf-8', errors='replace', creationflags=NO_WINDOW)
    if p.returncode:
        print(p.stdout[-4000:]); sys.exit('lab build failed')

    for f in mods.iterdir():
        f.unlink()
    shutil.copy2(package, mods / 'SurvivalcraftTravelMap.scmod')
    for extra in a.extra:
        shutil.copy2(extra, mods / Path(extra).name)
        print('extra mod', extra)
    with zipfile.ZipFile(mods / 'TravelMapLab.scmod', 'w') as z:
        z.write(build / 'out' / 'TravelMapLab.dll', 'TravelMapLab.dll')
        z.writestr('modinfo.json', json.dumps(dict(
            Name='TravelMapLab', Version='1.0.0', ApiVersion='1.9.2.1', ScVersion='2.4', PackageName='local.travelmaplab',
            Author='Tests', Description='Isolated tests only', LoadOrder=1300,
            Dependencies={info['PackageName']: info['Version']})))
    (runtime / 'lab-mode.txt').write_text(a.mode, encoding='ascii')
    worlds = runtime / 'doc' / 'Worlds'
    assert ROOT in worlds.parents
    if worlds.exists(): shutil.rmtree(worlds)
    res = runtime / 'lab-result.json'
    if res.exists(): res.unlink()
    shots = runtime / 'ScreenCapture'
    if shots.exists(): shutil.rmtree(shots)

    si = subprocess.STARTUPINFO(); si.dwFlags |= subprocess.STARTF_USESHOWWINDOW; si.wShowWindow = 0   # hidden window
    proc = subprocess.Popen([str(runtime / 'Survivalcraft.exe')], cwd=runtime, startupinfo=si)
    t0 = time.time()
    while time.time() - t0 < a.limit and not res.exists() and proc.poll() is None:
        time.sleep(2)
    time.sleep(3)
    if proc.poll() is None: proc.kill()
    print('elapsed', round(time.time() - t0), 'exit', proc.poll())
    print(res.read_text(encoding='utf-8')[-8000:] if res.exists() else 'NO RESULT')
    shot_files = sorted(shots.glob('*.png')) if shots.exists() else []
    print(len(shot_files), 'screenshots:', ', '.join(f.name for f in shot_files))
    for l in sorted(glob.glob(str(runtime / '*.log')) + glob.glob(str(runtime / 'Bugs/*')), key=os.path.getmtime)[-2:]:
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
