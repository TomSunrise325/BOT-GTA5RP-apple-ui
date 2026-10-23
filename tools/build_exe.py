"""Overlay the new UI on the original bundle and build one self-extracting BOT-GTA5RP.exe."""
import os, shutil, subprocess, sys, uuid, zipfile, pathlib, struct

work = pathlib.Path(sys.argv[1]).resolve()
stage = work / "stage"
src = work / "bot-src" / "bot"
tools = work / "_tools"
out = work / (sys.argv[2] if len(sys.argv) > 2 else "out")   # a running copy locks its exe: build elsewhere if needed
out.mkdir(exist_ok=True)

# 1) overlay the redesigned UI on the full bundle
for rel in ["templates", "static/style.css", "static/js"]:
    s, d = src / rel, stage / "bot" / rel
    if s.is_dir():
        shutil.copytree(s, d, dirs_exist_ok=True)
    else:
        shutil.copy2(s, d)
(stage / "bot" / "configs" / "log.txt").write_text("", encoding="utf-8")
for f in stage.glob("*.txt"):          # the old "how to run" note is replaced by the launcher itself
    f.unlink()

# 2) payload zip
payload = tools / "payload.zip"
with zipfile.ZipFile(payload, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for p in sorted(stage.rglob("*")):
        if p.is_file():
            z.write(p, p.relative_to(stage).as_posix())
print("payload MB:", round(payload.stat().st_size / 1048576, 1))

# 3) compile the stub
stub = tools / "stub.exe"
csc = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
icon = stage / "bot" / "static" / "icons" / "bot.ico"
cmd = [csc, "/nologo", "/target:winexe", "/optimize+", "/codepage:65001", f"/out:{stub}",
       "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
       "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll"]
cmd.append(f"/win32manifest:{tools / 'app.manifest'}")   # launcher asks for admin once; main.exe requires it too
if icon.exists():
    cmd.append(f"/win32icon:{icon}")
cmd += [str(tools / "Launcher.cs"), str(tools / "AutoE.cs")]
r = subprocess.run(cmd, capture_output=True, text=True)
print(r.stdout, r.stderr)
if r.returncode != 0 and icon.exists():       # retry without icon if csc rejects it
    cmd = [c for c in cmd if not c.startswith("/win32icon")]
    r = subprocess.run(cmd, capture_output=True, text=True)
    print("retry without icon:", r.stdout, r.stderr)
if r.returncode != 0:
    sys.exit("compile failed")

# 4) stub + payload + footer(len, guid, magic)
exe = out / "BOT-GTA5RP.exe"
with open(exe, "wb") as o:
    o.write(stub.read_bytes())
    with open(payload, "rb") as p:
        shutil.copyfileobj(p, o, 1 << 20)
    o.write(struct.pack("<q", payload.stat().st_size))
    o.write(uuid.uuid4().bytes_le)
    o.write(b"BOTPAY01")
print("built", exe, round(exe.stat().st_size / 1048576, 1), "MB")
