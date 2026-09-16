#!/usr/bin/env bash
# Mutation check. Each entry breaks ONE rule the suite claims to enforce; the suite has to
# notice. A mutation that survives means the assertion covering it is decorative.
#
#   ./mutate.sh        from _test/
#
# Not wired into deploy.ps1 -- it rewrites source files, so it is run by hand and on purpose.

set -u
cd "$(dirname "$0")/.." || exit 1

FILES="ReadClock.cs ProfileMath.cs RhythmMath.cs WaveMath.cs VwapMath.cs AcceptanceMath.cs AuctionMath.cs OrderflowMath.cs ReadModel.cs"

# One run at a time. Two of these racing each other share the same .bak files, so one restores
# the other's mutation mid-test and the result is a survivor that is really just a reverted
# edit.
lock="_test/.mutate.lock"
if ! mkdir "$lock" 2>/dev/null; then
    echo "another mutate.sh is running (remove $lock if it is not)" >&2
    exit 1
fi

# Restore whatever was mutated however this exits, including Ctrl-C and a kill.
cleanup() {
    for f in $FILES; do
        [ -f "$f.bak" ] && mv "$f.bak" "$f" && touch "$f"
    done
    rmdir "$lock" 2>/dev/null
}
trap cleanup EXIT INT TERM

survived=0
killed=0
skipped=0
broken=0

mutate() {
    local name="$1" file="$2" from="$3" to="$4"

    cp "$file" "$file.bak"

    # The match itself lives in mutate.pl: the files are CRLF and these patterns are LF, and
    # a multi-line match that quietly fails to apply reports SURVIVED against untouched code.
    perl _test/mutate.pl "$from" "$to" "$file"
    local applied=$?

    if [ "$applied" -eq 2 ]; then
        echo "SKIP      $name -- pattern not found, the code moved"
        mv "$file.bak" "$file"
        touch "$file"
        skipped=$((skipped + 1))
        return
    fi

    if [ "$applied" -ne 0 ]; then
        echo "SKIP      $name -- could not rewrite $file"
        mv "$file.bak" "$file"
        touch "$file"
        skipped=$((skipped + 1))
        return
    fi

    # Output goes to a file, NOT through $( ). Command substitution waits for every process
    # holding the pipe, and dotnet leaves a persistent VBCSCompiler build server behind that
    # inherits it -- so the substitution never returns and the run hangs on an already-finished
    # test. Redirecting sidesteps the inherited pipe entirely.
    local log="$PWD/_test/.mutate.log"
    (cd _test && dotnet run -c Release >"$log" 2>&1 </dev/null)

    if grep -q "All Ocean Read tests passed" "$log"; then
        echo "SURVIVED  $name"
        survived=$((survived + 1))
    elif grep -q "error CS" "$log"; then
        # A mutation that will not COMPILE proves nothing about the assertions, and counting it
        # as a kill is how a suite full of holes comes to look complete.
        echo "BROKEN    $name -- the mutated source did not compile"
        broken=$((broken + 1))
    elif grep -q "^FAIL" "$log"; then
        echo "killed    $name  <- $(grep '^FAIL' "$log" | head -2 | sed 's/^FAIL  //' | paste -sd'; ')"
        killed=$((killed + 1))
    else
        # It built and then fell over. That is the suite noticing, but a test that dereferences
        # a null instead of failing an assertion hides which rule broke, so it is worth fixing.
        echo "killed    $name  <- the suite crashed rather than failing an assertion"
        killed=$((killed + 1))
    fi

    # touch, or the restore is invisible to the next build. mv gives the file the BACKUP's
    # mtime, which predates the binary compiled from the mutated source, so MSBuild decides it
    # is up to date and the next run silently tests the PREVIOUS mutation. That shows up as a
    # clean suite failing straight after a clean mutation run, which reads like a real bug.
    mv "$file.bak" "$file"
    touch "$file"
}

# --- the clock ------------------------------------------------------------------------------
mutate "the futures day rolls AFTER 5 PM rather than at it" ReadClock.cs \
    "return local.TimeOfDay >= DayRoll ? local.Date.AddDays(1) : local.Date;" \
    "return local.TimeOfDay > DayRoll ? local.Date.AddDays(1) : local.Date;"

mutate "the halt is put at the 3 PM cash close" ReadClock.cs \
    "public const int HaltHour = 16;" \
    "public const int HaltHour = 15;"

mutate "cash hours include the 15:00 close" ReadClock.cs \
    "return local.TimeOfDay >= RegularOpen && local.TimeOfDay < RegularClose;
        }

        /// <summary>The overnight run-up" \
    "return local.TimeOfDay >= RegularOpen && local.TimeOfDay <= RegularClose;
        }

        /// <summary>The overnight run-up"

# --- the profile ----------------------------------------------------------------------------
mutate "a tie for the point of control takes whichever came first" ProfileMath.cs \
    "if (w > best ||
                   (w == best && Math.Abs(i - middle) < Math.Abs(bestIndex - middle)))" \
    "if (w > best)"

mutate "the value area walks one row at a time instead of two" ProfileMath.cs \
    "for (var i = 1; i <= 2 && hi + i < levels.Length; i++)" \
    "for (var i = 1; i <= 1 && hi + i < levels.Length; i++)"

mutate "two zero-width value areas never overlap" ProfileMath.cs \
    "if (union <= 0m) return aLow == bLow ? 1m : 0m;" \
    "if (union <= 0m) return 0m;"

mutate "single prints are counted at the extremes too" ProfileMath.cs \
    "var single = profile.Levels[i].Tpo == 1 && i >= first && i <= last;" \
    "var single = profile.Levels[i].Tpo == 1;"

mutate "shelves are not bridged across a one-tick dip" ProfileMath.cs \
    "else if (i - lastHit > gapTicks + 1)" \
    "else if (i - lastHit > gapTicks)"

mutate "trimming keeps the lightest shelves instead of the heaviest" ProfileMath.cs \
    "wide.Sort((a, b) => high ? b.Weight.CompareTo(a.Weight) : a.Weight.CompareTo(b.Weight));" \
    "wide.Sort((a, b) => a.Weight.CompareTo(b.Weight));"

mutate "any dip between shelves counts as a second distribution" ProfileMath.cs \
    "if (lower > 0m && valleyMax * 3m < lower) return true;" \
    "if (lower > 0m && valleyMax < lower) return true;"

mutate "a profile of nothing but single prints is all tail" ProfileMath.cs \
    "return ticks >= profile.Count ? 0 : ticks;" \
    "return ticks;"

mutate "an elongated profile is never recognised" ProfileMath.cs \
    "if (valueShare >= 0.80m) return ProfileShape.Elongated;" \
    "if (valueShare >= 0.99m) return ProfileShape.Elongated;"

mutate "a poor extreme needs only one bracket" ProfileMath.cs \
    "if (profile.Levels[index].Tpo < minTpo) return false;" \
    "if (profile.Levels[index].Tpo < 1) return false;"

# --- rotations and rhythm -------------------------------------------------------------------
mutate "an outside bar confirms its own reversal" RhythmMath.cs \
    "if (!extended && _extreme - low >= threshold)" \
    "if (_extreme - low >= threshold)"

mutate "an outside bar confirms its own reversal, going down" RhythmMath.cs \
    "if (!deepened && high - _extreme >= threshold)" \
    "if (high - _extreme >= threshold)"

mutate "the first leg runs from the wrong end of the opening range" RhythmMath.cs \
    "if (_seedHighBar > _seedLowBar)" \
    "if (_seedHighBar < _seedLowBar)"

mutate "leg maturity takes the nearer reading instead of the further" RhythmMath.cs \
    "var worst = Math.Max(read.TickPercentile, read.MinutePercentile);" \
    "var worst = Math.Min(read.TickPercentile, read.MinutePercentile);"

mutate "quantiles step by whole samples instead of interpolating" RhythmMath.cs \
    "var weight = position - lower;

            return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;" \
    "return sorted[lower];"

mutate "a rhythm is reported from any number of rotations" RhythmMath.cs \
    "if (count < minLegs) return rhythm;" \
    "if (count < 1) return rhythm;"

mutate "efficiency measures the sum of the ends, not the distance between them" RhythmMath.cs \
    "var net = Math.Abs(end - start);" \
    "var net = Math.Abs(end + start);"

mutate "true range ignores the gap from the previous close" RhythmMath.cs \
    "sum += Math.Max(range, Math.Max(upGap, downGap));" \
    "sum += range;"

# --- waves ----------------------------------------------------------------------------------
mutate "wave 3 must be the longest rather than not the shortest" WaveMath.cs \
    "w3 >= w1 || w3 >= w5," \
    "w3 >= w1 && w3 >= w5,"

mutate "wave 4 is allowed back into wave 1" WaveMath.cs \
    "Rule(count, \"wave 4 stays out of wave 1\",
                 Move(1, 4) > 0m," \
    "Rule(count, \"wave 4 stays out of wave 1\",
                 true,"

mutate "a truncated fifth is flagged the wrong way round" WaveMath.cs \
    "count.Truncated = Move(3, 5) <= 0m;" \
    "count.Truncated = Move(3, 5) >= 0m;"

mutate "any contact between same-direction legs reads as corrective" WaveMath.cs \
    "if (ProfileMath.Overlap(nowLow, nowHigh, wasLow, wasHigh) >= Shared) overlaps++;" \
    "if (ProfileMath.Overlap(nowLow, nowHigh, wasLow, wasHigh) > 0m) overlaps++;"

# --- VWAP -----------------------------------------------------------------------------------
mutate "the deviation forgets to subtract the mean" VwapMath.cs \
    "var variance = _p2v / _v - Value * Value;" \
    "var variance = _p2v / _v;"

mutate "an anchor never restarts on a new period" VwapMath.cs \
    "if (track.Bars == 0 || track.Key != key)" \
    "if (track.Bars == 0)"

# --- acceptance -----------------------------------------------------------------------------
mutate "a test opens before there is a side to break from" AcceptanceMath.cs \
    "if (was != 0 && side != 0 && side != was && !_live.ContainsKey(reference.Name))" \
    "if (side != 0 && !_live.ContainsKey(reference.Name))"

mutate "acceptance needs time OR ground rather than both" AcceptanceMath.cs \
    "if (test.MinutesBeyond >= test.MinutesNeeded && test.MaxGroundTicks >= test.GroundNeeded)" \
    "if (test.MinutesBeyond >= test.MinutesNeeded || test.MaxGroundTicks >= test.GroundNeeded)"

mutate "a wick beyond counts as holding beyond" AcceptanceMath.cs \
    "var beyond = test.Up ? close > test.Price : close < test.Price;" \
    "var beyond = test.Up ? high > test.Price : low < test.Price;"

mutate "acceptance is judged with no rhythm behind it" AcceptanceMath.cs \
    "if (minutesNeeded > 0d && groundNeeded > 0m)" \
    "if (true)"

mutate "the nearest level ignores how far away it is" AcceptanceMath.cs \
    "if (distance > withinTicks) continue;" \
    "if (distance > decimal.MaxValue) continue;"

# --- auction theory -------------------------------------------------------------------------
mutate "a disagreement between the two measurements gets a casting vote" AuctionMath.cs \
    "return Condition.Transitioning;
        }

        /// <summary>
        /// The day type" \
    "return travelSays;
        }

        /// <summary>
        /// The day type"

mutate "value unchanged needs an exact match" AuctionMath.cs \
    "if (Math.Abs(todayHigh - priorHigh) <= tolerance &&
                Math.Abs(todayLow - priorLow) <= tolerance)" \
    "if (todayHigh == priorHigh && todayLow == priorLow)"

mutate "two distributions lose to a range extension" AuctionMath.cs \
    "if (shape == ProfileShape.DoubleDistribution)
            {
                why = \"two shelves" \
    "if (shape == ProfileShape.Elongated)
            {
                why = \"two shelves"

mutate "extension either way counts as a neutral day" AuctionMath.cs \
    "if (extendedUp && extendedDown)" \
    "if (extendedUp || extendedDown)"

mutate "the value migration window looks forward instead of back" AuctionMath.cs \
    "var cutoff = last.Time.AddMinutes(-overMinutes);" \
    "var cutoff = last.Time.AddMinutes(overMinutes);"

# --- orderflow ------------------------------------------------------------------------------
mutate "a breakout counts as absorption" OrderflowMath.cs \
    "if (Math.Abs(groundTicks) > maxGroundTicks) return false;" \
    "if (Math.Abs(groundTicks) < maxGroundTicks) return false;"

mutate "rising open interest on a rally reads as short covering" OrderflowMath.cs \
    "if (priceChangeTicks > 0m) return oiChange > 0m ? OiRead.NewLongs : OiRead.ShortCovering;" \
    "if (priceChangeTicks > 0m) return oiChange > 0m ? OiRead.ShortCovering : OiRead.NewLongs;"

mutate "a high the tape paid for still reads as a divergence" OrderflowMath.cs \
    "if (b.CumulativeDelta >= a.CumulativeDelta) return false;" \
    "if (b.CumulativeDelta <= a.CumulativeDelta) return false;"

mutate "a chart with no tape reads as the tape disagreeing" OrderflowMath.cs \
    "if (forCount == 0 && againstCount == 0) return FlowVerdict.Silent;" \
    "if (forCount == 0 && againstCount == 0) return FlowVerdict.Diverges;"

mutate "absorption is credited to the side doing the aggressing" OrderflowMath.cs \
    "var buyersAggressing = flow.AtLevel.AskShare > 0.5d;" \
    "var buyersAggressing = flow.AtLevel.AskShare < 0.5d;"

mutate "delta under the threshold still gets a vote" OrderflowMath.cs \
    "if (flow.HaveFootprint && Math.Abs(flow.LegDelta) >= minDelta)" \
    "if (flow.HaveFootprint)"

# --- the read itself ------------------------------------------------------------------------
mutate "one missing condition is still an ACT" ReadModel.cs \
    "if (failed == 0)
            {
                box.Stance = Stance.Act;" \
    "if (failed <= 1)
            {
                box.Stance = Stance.Act;"

mutate "the read is assembled with no measured rhythm" ReadModel.cs \
    "if (!input.Rhythm.Valid)
            {
                box.Playbook = Playbook.StandAside;" \
    "if (false)
            {
                box.Playbook = Playbook.StandAside;"

mutate "an unresolved clock does not stop the read" ReadModel.cs \
    "if (input.ClockError != null)
            {
                box.Add(\"CLOCK\"" \
    "if (false)
            {
                box.Add(\"CLOCK\""

mutate "price decimals give up after one place" ReadModel.cs \
    "while (value != Math.Floor(value) && decimals < 8)" \
    "while (value != Math.Floor(value) && decimals < 1)"

echo
echo "$killed killed, $survived survived, $skipped skipped, $broken broken"
[ "$survived" -eq 0 ] && [ "$skipped" -eq 0 ] && [ "$broken" -eq 0 ]
