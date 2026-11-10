# Builds GameImporter.dll on Windows with the C# compiler from Visual Studio 2022 (Build Tools are enough).
#   python tools\build_win.py [path\to\Valheim] [out.dll]
# Default Valheim path: the Steam install. BepInEx must already be installed there (BepInEx\core\BepInEx.dll).
import glob, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
VAL = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files (x86)\Steam\steamapps\common\Valheim"
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "..", "GameImporter.dll")
CSC = next((c for c in glob.glob(r"C:\Program Files*\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe")), None)
if not CSC:
    sys.exit("csc.exe not found - install Visual Studio 2022 or the Build Tools (C# workload)")
refs = glob.glob(os.path.join(VAL, "valheim_Data", "Managed", "*.dll")) + [os.path.join(VAL, "BepInEx", "core", "BepInEx.dll"),
                                                                          os.path.join(VAL, "BepInEx", "core", "0Harmony.dll")]
rsp = os.path.join(HERE, "build.rsp")
with open(rsp, "w", encoding="utf-8") as f:
    f.write("/nologo /target:library /nostdlib+ /langversion:latest /optimize+ /nowarn:CS0618,CS0436,CS1701,CS1702\n")
    f.write('/out:"%s"\n' % os.path.abspath(OUT))
    for r in refs:
        f.write('/reference:"%s"\n' % r)
    for c in sorted(glob.glob(os.path.join(HERE, "..", "src", "*.cs"))):
        f.write('"%s"\n' % os.path.abspath(c))
p = subprocess.run([CSC, "/noconfig", "@" + rsp])
os.remove(rsp)
sys.exit(p.returncode)
