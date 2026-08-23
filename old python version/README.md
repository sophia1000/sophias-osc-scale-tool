# Archived Python Version

This directory contains the previous Python implementation preserved while the
application is being rewritten in C#. The root JSON files remain outside this
archive so the new application can migrate/read the existing settings:

- `../vrc_height_osc_config.json` - active version 3 configuration
- `../height_rules.json` - legacy/sample rule data

## Main height OSC tool

From this directory, run:

```bat
start.bat
```

The launcher runs `vrc_height_osc.py` and expects the Python dependencies used
by the old version to be installed in the active Python environment.

## SteamVR playspace helper

The separate helper is under `playspace test`. From this archive directory,
run:

```bat
playspace test\start.bat
```

That launcher runs `playspace test\vrc_eye_height_lock.py`. It is independent
of the main height OSC tool and requires its Python OSC and OpenVR dependencies.
