using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace FootIk;

public sealed unsafe partial class Plugin
{
    // ICondition only ever describes the local player, so the flags split: a mount, a jump or a dive of ours says
    // nothing about anyone else, while a cutscene or a zone load stops the plugin for everyone.
    private readonly ConditionFlag[] gateFlags =
    [
        ConditionFlag.Jumping, ConditionFlag.Mounted, ConditionFlag.Swimming, ConditionFlag.Diving, ConditionFlag.InFlight,
    ];

    private readonly ConditionFlag[] globalFlags = [ConditionFlag.WatchingCutscene, ConditionFlag.BetweenAreas];

    private void Tick()
    {
        var now = this.clock.Elapsed.TotalSeconds;
        var rawDt = now - this.lastTick;
        var dt = (float)Math.Clamp(rawDt, 0, 0.1);
        this.lastTick = now;

        foreach (var known in this.states.Values)
        {
            known.Seen = false;
        }

        var snap = default(Snapshot);
        var lp = Objects.LocalPlayer;
        var localAddr = lp?.Address ?? 0;
        if (localAddr != 0)
        {
            var st = this.StateFor(localAddr, lp!.GameObjectId);
            st.Seen = true;
            this.TickOne((Character*)localAddr, st, dt, rawDt, local: true, retire: false, ref snap);
        }

        this.Snap = snap;
        this.Sweep(localAddr, lp, dt, rawDt);
    }

    // Everyone else: nearest first inside the radius, up to the budget, which is a count rather than a time slice
    // because the raycasts are the cost and a count bounds them. A character we already hold state for is ticked
    // whether it is picked or not, with the gate closed if it is not, so our offsets come off before it is dropped.
    private void Sweep(nint localAddr, IGameObject? lp, float dt, double rawDt)
    {
        var c = this.Settings;
        var cap = lp == null || !c.Others ? 0 : Math.Clamp(c.MaxOthers, 0, MaxTracked);
        this.Tracked = localAddr == 0 ? 0 : 1;
        if (cap == 0 && this.states.Count <= this.Tracked)
        {
            return;
        }

        Span<nint> picked = stackalloc nint[MaxTracked];
        Span<ulong> ids = stackalloc ulong[MaxTracked];
        Span<float> dist = stackalloc float[MaxTracked];
        var origin = lp?.Position ?? default;
        var radius2 = c.OthersRadius * c.OthersRadius;
        var n = 0;
        // Indexed rather than enumerated: IObjectTable.GetEnumerator returns the interface, which allocates per frame.
        for (var i = 0; i < Objects.Length; i++)
        {
            var o = Objects[i];
            if (o == null || o.Address == 0 || o.Address == localAddr)
            {
                continue;
            }

            var addr = o.Address;

            // Marked before the filters: a character that stops qualifying must still be ticked down, not dropped
            // while our offsets are still on it.
            if (this.states.TryGetValue(addr, out var known) && known.Id == o.GameObjectId)
            {
                known.Seen = true;
            }

            if (cap == 0 || !Eligible(o, c.Who))
            {
                continue;
            }

            var d = Vector3.DistanceSquared(o.Position, origin);
            if (d > radius2 || (n == cap && d >= dist[n - 1]))
            {
                continue;
            }

            var at = n < cap ? n++ : cap - 1;
            while (at > 0 && dist[at - 1] > d)
            {
                dist[at] = dist[at - 1];
                picked[at] = picked[at - 1];
                ids[at] = ids[at - 1];
                at--;
            }

            dist[at] = d;
            picked[at] = addr;
            ids[at] = o.GameObjectId;
        }

        for (var i = 0; i < n; i++)
        {
            var st = this.StateFor(picked[i], ids[i]);
            st.Seen = true;
            var other = default(Snapshot);
            this.TickOne((Character*)picked[i], st, dt, rawDt, local: false, retire: false, ref other);
        }

        this.Tracked += n;
        this.Retire(picked[..n], localAddr, dt, rawDt);
    }

    // A non-humanoid NPC holds a slot until its chain fails to resolve, so NPCs go off wholesale rather than one kind
    // at a time. Party, alliance and friend are read off the character itself (Dalamud's StatusFlags over the game's
    // relation byte, the same one that draws the friend icon on a nameplate), so nothing depends on the player having
    // opened a social window.
    private static bool Eligible(IGameObject o, Who who)
    {
        if (who == Who.Everyone)
        {
            return o.ObjectKind is ObjectKind.Pc or ObjectKind.BattleNpc or ObjectKind.EventNpc;
        }

        if (o.ObjectKind != ObjectKind.Pc)
        {
            return false;
        }

        if (who == Who.Players)
        {
            return true;
        }

        var want = StatusFlags.PartyMember | StatusFlags.AllianceMember;
        if (who != Who.Party)
        {
            want |= StatusFlags.Friend;
        }

        return o is ICharacter ch && (ch.StatusFlags & want) != 0;
    }

    private void Retire(scoped ReadOnlySpan<nint> picked, nint localAddr, float dt, double rawDt)
    {
        foreach (var (addr, st) in this.states)
        {
            if (addr == localAddr || picked.Contains(addr))
            {
                continue;
            }

            if (st.Seen)
            {
                var snap = default(Snapshot);
                this.TickOne((Character*)addr, st, dt, rawDt, local: false, retire: true, ref snap);
            }

            // Unseen means the object is gone and our offsets with it; seen and idle means they have come back off.
            if (!st.Seen || st.Idle)
            {
                this.states.Remove(addr);
            }
        }
    }

    private CharState StateFor(nint address, ulong id)
    {
        if (this.states.TryGetValue(address, out var st))
        {
            if (st.Id == id)
            {
                return st;
            }

            // The allocator handed this address to a different spawn: nothing we tracked applies to the new one.
            this.states.Remove(address);
        }

        st = new CharState { Id = id };
        this.states[address] = st;
        return st;
    }
}
