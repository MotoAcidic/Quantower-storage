#!/usr/bin/env bash
# Mutation check. Each entry breaks ONE rule the suite claims to enforce; the suite has to
# notice. A mutation that survives means the assertion covering it is decorative.
#
#   ./mutate.sh        from _test/
#
# Not wired into deploy.ps1 -- it rewrites source files, so it is run by hand and on purpose.

set -u
cd "$(dirname "$0")/.." || exit 1

# One run at a time. Two of these racing each other share the same .bak files, so one restores
# the other's mutation mid-test and the result is a survivor that is really just a reverted
# edit. Ask how that was found out.
lock="_test/.mutate.lock"
if ! mkdir "$lock" 2>/dev/null; then
    echo "another mutate.sh is running (remove $lock if it is not)" >&2
    exit 1
fi

# Restore whatever was mutated however this exits, including Ctrl-C and a kill.
cleanup() {
    for f in DevelopedMath.cs DevelopedClock.cs; do
        [ -f "$f.bak" ] && mv "$f.bak" "$f" && touch "$f"
    done
    rmdir "$lock" 2>/dev/null
}
trap cleanup EXIT INT TERM

survived=0
killed=0

mutate() {
    local name="$1" file="$2" from="$3" to="$4"

    cp "$file" "$file.bak"

    if ! grep -qF -- "$from" "$file"; then
        echo "SKIP  $name -- pattern not found, the code moved"
        rm -f "$file.bak"
        return
    fi

    # Only the first occurrence, so a mutation stays surgical.
    perl -0pi -e 'BEGIN{$f=shift;$t=shift} s/\Q$f\E/$t/' "$from" "$to" "$file"

    # Output goes to a file, NOT through $( ). Command substitution waits for every process
    # holding the pipe, and `dotnet` leaves a persistent VBCSCompiler build server behind that
    # inherits it -- so the substitution never returns and the run hangs on an already-finished
    # test. Redirecting sidesteps the inherited pipe entirely.
    local log="$PWD/_test/.mutate.log"
    (cd _test && dotnet run -c Release >"$log" 2>&1 </dev/null)

    local out
    out=$(tail -40 "$log")

    if grep -q "All Ocean Developed tests passed" "$log"; then
        echo "SURVIVED  $name"
        survived=$((survived + 1))
    else
        echo "killed    $name  <- $(echo "$out" | grep '^FAIL' | head -2 | sed 's/^FAIL: //' | paste -sd'; ')"
        killed=$((killed + 1))
    fi

    # touch, or the restore is invisible to the next build. mv gives the file the BACKUP's
    # mtime, which predates the binary compiled from the mutated source, so MSBuild decides it
    # is up to date and the next run silently tests the PREVIOUS mutation. That shows up as a
    # clean suite failing straight after a clean mutation run, which reads like a real bug.
    mv "$file.bak" "$file"
    touch "$file"
}

# --- the point of control -------------------------------------------------------------------
mutate "poc tie takes the higher price" DevelopedMath.cs \
    "if (level.Volume > profile.MaxLevelVolume)" \
    "if (level.Volume >= profile.MaxLevelVolume)"

# --- the dense ladder -----------------------------------------------------------------------
mutate "ladder built sparse, one entry per traded price" DevelopedMath.cs \
    "var span = (int)Math.Round((high - low) / tickSize, MidpointRounding.AwayFromZero) + 1;
            if (span <= 0 || span > maxLevels) return null;" \
    "var span = _levels.Count;
            if (span <= 0 || span > maxLevels) return null;"

# --- refuse, never truncate -----------------------------------------------------------------
mutate "an over-wide profile is clipped instead of refused" DevelopedMath.cs \
    "if (span <= 0 || span > maxLevels) return null;

            var profile = new Profile();" \
    "if (span <= 0) return null;
            if (span > maxLevels) span = maxLevels;

            var profile = new Profile();"

# --- summing days ---------------------------------------------------------------------------
mutate "merge ignores each day's own price offset" DevelopedMath.cs \
    "var offset = (int)Math.Round((part.LowPrice - low) / tickSize, MidpointRounding.AwayFromZero);" \
    "var offset = 0;"

mutate "merge loses a half-recorded day's warning" DevelopedMath.cs \
    "if (!part.Complete) merged.Complete = false;" \
    "if (false) merged.Complete = false;"

mutate "merge miscounts the days it summed" DevelopedMath.cs \
    "merged.Days += part.Days > 0 ? part.Days : 1;" \
    "merged.Days = part.Days > 0 ? part.Days : 1;"

# --- shelves --------------------------------------------------------------------------------
mutate "shelf bridging reaches one tick short" DevelopedMath.cs \
    "for (var j = i + 1; j <= i + gapTicks && j < levels.Length; j++)" \
    "for (var j = i + 1; j < i + gapTicks && j < levels.Length; j++)"

mutate "thin shelves are kept" DevelopedMath.cs \
    "if (zones[i].Ticks < minTicks) zones.RemoveAt(i);" \
    "if (zones[i].Ticks < 0) zones.RemoveAt(i);"

mutate "shelves are trimmed by price rather than by weight" DevelopedMath.cs \
    "zones.Sort(delegate (HvnZone a, HvnZone b) { return b.Volume.CompareTo(a.Volume); });
                zones.RemoveRange(maxZones, zones.Count - maxZones);" \
    "zones.RemoveRange(maxZones, zones.Count - maxZones);"

mutate "shelf peak tie takes the higher price" DevelopedMath.cs \
    "if (profile.Levels[i].Volume <= zone.PeakVolume) continue;" \
    "if (profile.Levels[i].Volume < zone.PeakVolume) continue;"

mutate "shelf totals only count the qualifying levels" DevelopedMath.cs \
    "zone.Volume += profile.Levels[i].Volume;" \
    "if (i == from || i == to) zone.Volume += profile.Levels[i].Volume;"

# --- display folding ------------------------------------------------------------------------
mutate "folded rows overlap by a tick" DevelopedMath.cs \
    "var from = r * ticksPerRow;" \
    "var from = r * ticksPerRow == 0 ? 0 : r * ticksPerRow - 1;"

mutate "the value area flag is read off the folded row" DevelopedMath.cs \
    "if (profile.ValIndex >= 0 && i >= profile.ValIndex && i <= profile.VahIndex)
                        row.InValueArea = true;" \
    "if (profile.ValIndex >= 0 && i > profile.ValIndex && i < profile.VahIndex)
                        row.InValueArea = true;"

# --- the value area -------------------------------------------------------------------------
mutate "the value area grows toward the thinner side" DevelopedMath.cs \
    "if (canUp && (!canDown || up >= down))" \
    "if (canUp && (!canDown || up <= down))"

# --- the clock ------------------------------------------------------------------------------
mutate "the futures day rolls at midnight" DevelopedClock.cs \
    "return local.TimeOfDay >= DayRoll
                 ? local.Date.AddDays(1)
                 : local.Date;" \
    "return local.Date;"

mutate "5 PM itself falls on the wrong side of the roll" DevelopedClock.cs \
    "return local.TimeOfDay >= DayRoll" \
    "return local.TimeOfDay > DayRoll"

mutate "the week is cut on Sunday rather than Monday" DevelopedClock.cs \
    "var offset = ((int)date.DayOfWeek + 6) % 7;" \
    "var offset = (int)date.DayOfWeek;"

mutate "the month is a fixed thirty days" DevelopedClock.cs \
    "case PeriodKind.PrevMonth: return CloseOf(key.AddMonths(1).AddDays(-1));" \
    "case PeriodKind.PrevMonth: return CloseOf(key.AddDays(29));"

mutate "a trade date opens at its own 5 PM rather than the evening before" DevelopedClock.cs \
    "return tradeDate.Date.AddDays(-1).Add(DayRoll);" \
    "return tradeDate.Date.Add(DayRoll);"

# --- the two that survived the first pass ---------------------------------------------------
mutate "the kept shelves are never re-sorted into price order" DevelopedMath.cs     "zones.Sort(delegate (HvnZone a, HvnZone b) { return a.FromIndex.CompareTo(b.FromIndex); });
            return zones.ToArray();"     "return zones.ToArray();"

echo
echo "killed $killed, survived $survived"
[ "$survived" -eq 0 ]
