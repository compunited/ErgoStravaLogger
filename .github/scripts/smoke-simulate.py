"""Validate a published app using only synthetic training data and local files."""

import json
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path


application = Path(sys.argv[1]).resolve()
command = ["dotnet", str(application)] if application.suffix == ".dll" else [str(application)]
with tempfile.TemporaryDirectory(prefix="ergo-simulate-") as temporary:
    directory = Path(temporary)
    settings = directory / "config.json"
    settings.write_text(json.dumps({
        "ErgoIp": "127.0.0.1",
        "RecordingEnabled": False,
        "OutputDirectory": str(directory / "data"),
        "Strava": {"Enabled": False, "TokenFile": str(directory / "tokens.json")},
        "Zwift": {"Enabled": False, "ControlEnabled": False}
    }), encoding="utf-8")
    subprocess.run(command + ["simulate", "--config", str(settings)],
                   check=True, cwd=directory, timeout=60)
    recordings = list((directory / "data" / "simulation").glob("*.tcx"))
    if len(recordings) != 1:
        raise RuntimeError("Simulation did not produce exactly one TCX recording")
    tree = ET.parse(recordings[0])
    points = tree.findall(".//{*}Trackpoint")
    if len(points) != 12:
        raise RuntimeError(f"Expected 12 synthetic trackpoints, got {len(points)}")
    if (directory / "tokens.json").exists():
        raise RuntimeError("Offline simulation must not store Strava tokens")
    print("Offline simulation passed: TCX with 12 synthetic trackpoints.")
