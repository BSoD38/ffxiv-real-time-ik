using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace FootIk;

// The damage grunt of whoever was shoved, asked of the character rather than built here: LoadCharacterSound picks the
// voice off the container it is called on, which carries the id chosen at creation, so nothing here knows or needs to
// know a character's race, sex, language or voice.
//
// The argument shape is the game's own, captured from its call on fall damage: every argument but the sound id is
// zero. That id is NOT the group number PlaySound takes - the game plays 29 for the grunt and 26 for the landing
// thud, both confirmed by ear, while 1 is PlaySound's damage group and silent here. Passing 1 is what made a first
// attempt look as though this function only loaded and never played, and sent the whole thing down a blind alley:
// reading the race's own voice file by path, which cost a nine-code race table, a two-bank guess and a learned map of
// voice to bank, and still could not reach more than two of the twelve creation voices. All of it deleted with this.
//
// Never called from the render detour. A foreign call on a pointer we had misidentified once took the client down,
// and an access violation is not catchable in .NET, so the detour only records who should grunt and the call happens
// here on the framework thread. Both run on the game main thread, so the queue needs no synchronisation.
public sealed unsafe partial class Plugin
{
    private const int DamageGrunt = 29;
    private const int MaxGrunts = 8;

    // Two bodies grunt per bump, so this is three bumps a second sustained, with a little room for a pile-up. The bump
    // cooldowns bound one character, never the total.
    private const float GruntsPerSecond = 6f;
    private const float GruntBurst = 8f;

    private readonly (nint Addr, ulong Id)[] grunts = new (nint Addr, ulong Id)[MaxGrunts];
    private int gruntCount;
    private int gruntFaults;
    private double gruntBudgetAt;
    private float gruntBudget = GruntBurst;

    private bool gruntTripped;

    // PlaySound resolves a game path, never a file, so the shipped container sits behind a path of our own through a
    // Penumbra temporary mod. It is a battle-voice container with the game's audio cut off and its damage group's four
    // takes pointed at our four clips, so group 1 rolls between them itself, at equal weights. Its sound entries were moved
    // from the voice bus to the sound-effects bus (byte 1 of each entry is the SoundBus).
    private const string ShoveSoundPath = "sound/vfx/footik/shove.scd";
    private const string ShoveSoundTag = "FootIk.ShoveSound";
    private const uint ShoveSoundGroup = 1;
    private const double ShoveSoundGap = 0.15;
    private const double ShoveSoundRetry = 5;

    private Vector3? shoveSoundAt;
    private bool shoveSoundReady;
    private double shoveSoundNext;

    private void WantGrunt(nint addr, ulong id)
    {
        if (!this.Settings.Grunt || this.gruntTripped || addr == 0 || this.gruntCount >= MaxGrunts)
        {
            return;
        }

        // Both sides of a bump can be found twice in one frame, once from each body's own scan.
        for (var i = 0; i < this.gruntCount; i++)
        {
            if (this.grunts[i].Addr == addr)
            {
                return;
            }
        }

        this.grunts[this.gruntCount++] = (addr, id);
    }

    private void PlaySounds(IFramework _)
    {
        this.PlayShoveSound();

        var queued = this.gruntCount;
        this.gruntCount = 0;
        if (queued == 0 || this.gruntTripped)
        {
            return;
        }

        try
        {
            if (VfxContainer.Addresses.LoadCharacterSound.Value == 0)
            {
                Log.Error("FootIk: LoadCharacterSound did not resolve; grunts are off");
                this.gruntTripped = true;
                return;
            }

            var now = this.clock.Elapsed.TotalSeconds;
            this.gruntBudget = MathF.Min(GruntBurst, this.gruntBudget + (float)((now - this.gruntBudgetAt) * GruntsPerSecond));
            this.gruntBudgetAt = now;
            for (var i = 0; i < queued && this.gruntBudget >= 1f; i++)
            {
                this.gruntBudget -= 1f;
                this.Grunt(this.grunts[i].Addr, this.grunts[i].Id);
            }
        }
        catch (Exception ex)
        {
            this.gruntFaults++;
            if (this.gruntFaults >= 5)
            {
                this.gruntTripped = true;
                Log.Error(ex, "FootIk: grunts off after {Faults} faults", this.gruntFaults);
            }
        }
    }

    // The allocator reuses character addresses, so the queued one is believed only while the object table still agrees
    // it holds that spawn; a character with nothing drawn has no voice to play.
    private void Grunt(nint addr, ulong id)
    {
        for (var i = 0; i < Objects.Length; i++)
        {
            var o = Objects[i];
            if (o == null || o.Address != addr || o.GameObjectId != id)
            {
                continue;
            }

            var chr = (Character*)addr;
            if (chr->DrawObject == null)
            {
                return;
            }

            // autoRelease is the one argument deliberately not the game's own. The game passes 0 because it keeps the
            // SoundData* it gets back and frees it later; we drop ours on the floor, and passing 0 filled the mixer
            // until the game's own sounds stopped playing (twice in game, 2026-09-14, once on each playback route).
            // 1 hands that job to the engine. If it ever turns out to be ignored here, the other way is to keep the
            // returned pointer and call SoundManager.ReleaseSoundData on it, which is what the game is doing.
            chr->Vfx.LoadCharacterSound(DamageGrunt, 0, nint.Zero, 1, 0, 0, 0);
            return;
        }
    }

    private void PlayShoveSound()
    {
        var at = this.shoveSoundAt;
        this.shoveSoundAt = null;
        var now = this.clock.Elapsed.TotalSeconds;
        if (!this.Settings.ShoveSound || now < this.shoveSoundNext)
        {
            return;
        }

        try
        {
            if (!this.shoveSoundReady)
            {
                this.shoveSoundReady = RedirectShoveSound();
                this.shoveSoundNext = now + ShoveSoundRetry;
                if (this.shoveSoundReady)
                {
                    // The first request for a path only starts loading it, so a silent one now keeps the first shove.
                    PlayShove(0f, Vector3.Zero, false);
                    this.shoveSoundNext = now + ShoveSoundGap;
                }

                return;
            }

            if (at is { } p)
            {
                PlayShove(this.Settings.ShoveVolume, p, true);
                this.shoveSoundNext = now + ShoveSoundGap;
            }
        }
        catch (Exception ex)
        {
            this.shoveSoundNext = double.PositiveInfinity;
            Log.Error(ex, "FootIk: impact sound off");
        }
    }

    private static bool RedirectShoveSound()
    {
        var file = Path.Combine(PluginInterface.AssemblyLocation.DirectoryName!, "sounds", "shove.scd");
        try
        {
            var result = PluginInterface.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5")
                .InvokeFunc(ShoveSoundTag, new() { [ShoveSoundPath] = file }, string.Empty, 0);
            Log.Information("FootIk: impact sound {Path} -> {File}, Penumbra returned {Result}", ShoveSoundPath, file, result);
            return result == 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FootIk: impact sound waits for Penumbra");
            return false;
        }
    }

    // autoRelease: nothing here keeps the SoundData*, and the game's own false leaks a mixer voice per call.
    private static void PlayShove(float volume, Vector3 at, bool positional)
    {
        var sound = SoundManager.Instance();
        if (sound != null)
        {
            sound->PlaySound(ShoveSoundPath, volume, 0u, at.X, at.Y, at.Z, 1f, 0, ShoveSoundGroup, true, SoundVolumeCategory.Player, false, -1, false, false, positional, false);
        }
    }

    // On the framework thread, from Teardown: Penumbra's IPC is not safe anywhere else.
    private void DropShoveSound()
    {
        if (!this.shoveSoundReady)
        {
            return;
        }

        try
        {
            PluginInterface.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(ShoveSoundTag, 0);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FootIk: could not drop the impact sound redirect");
        }
    }
}
