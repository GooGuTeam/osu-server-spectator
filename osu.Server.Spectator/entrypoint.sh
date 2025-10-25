#!/bin/bash
set -e

mkdir -p /app/rulesets

for f in /tmp/rulesets/*.dll; do
    if [ -f "$f" ]; then
        dest="/app/rulesets/$(basename "$f")"
        if [ ! -f "$dest" ]; then
            echo "Copying $(basename "$f") to /app/rulesets"
            cp "$f" "$dest"
        else
            echo "$(basename "$f") already exists, skipping"
        fi
    fi
done

exec dotnet /app/osu.Server.Spectator.dll
