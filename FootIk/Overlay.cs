using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace FootIk;

// The settings window is built from native game nodes. They are destroyed when the window closes, so every node
// reference is rebuilt in OnSetup and dropped in OnFinalize; nothing here survives a close.
internal sealed class Overlay : NativeAddon
{
    private const float RowHeight = 24f;

    // Every native widget in a row this tall centres its contents four pixels above the middle: a checkbox sits its
    // box at y=2 and gives its own label a height of 20, a slider sizes its handle to Height - 4 at y=0. A label
    // given the full row height centres two pixels below all of them and reads visibly low beside one.
    private const float TextHeight = RowHeight - 4f;
    private const float BarHeight = 28f;
    private const float Gap = 4f;
    private const float RowGap = 8f; // between one setting and the next, where Gap is inside a single one
    private const float LabelWidth = 160f; // the name column of the Status tables, not the settings themselves
    private const float ValueWidth = 116f;
    // Seven tabs share the bar, and "Performance" is the one that has to fit: the window is sized around it.
    private const float ResetWidth = 88f;
    private const float ScrollBarRoom = 10f;
    private const float LineHeight = 17f;
    private const long SaveDelayMs = 500;

    // Asides and column headings, the native stand-in for ImGui's TextDisabled.
    private static readonly Vector4 Dim = new(0.62f, 0.62f, 0.62f, 1f);

    public required Plugin Owner { get; init; }

    // Rebuilt with the active tab: a text node that restates a value, and a widget that is greyed out while
    // the setting above it is off. `Shown` is the text each node already carries, so an unchanged line is not
    // written into the game's node every frame; `Applied` is the same for a gate.
    private readonly List<(TextNode Node, Func<string> Text, string Shown)> live = [];
    private readonly List<(Func<bool> On, Action<bool> Set, bool? Applied)> gates = [];

    // A slider component owns its value node: it puts its own raw step count back into it on every update, so the
    // count cannot be hidden. It is overwritten instead, unconditionally rather than only when the string changes,
    // because the component rewrites it too.
    private readonly List<(TextNode Node, Func<string> Text)> forced = [];

    // Which folds the user has opened, keyed by tab so that "Advanced" on one tab is not "Advanced" on another.
    private readonly HashSet<string> unfolded = [];

    private (string Name, Func<List<NodeBase>> Build, string[]? Fields)[]? tabs;
    private ScrollingNode<VerticalListNode>? scroll;
    private TextButtonNode? resetButton;
    private ConfirmWindow? confirm;
    private float rowWidth;
    private int tab;
    private bool setup;
    private bool rebuild;
    private bool dirty;
    private long dirtyAt;
    private float rawPeak;
    private float lostPeak;

    // One row per tab: its name on the bar, what builds it, and what its reset button puts back. The Status tab has no
    // field list and resets everything instead. nameof so renaming a setting breaks the build rather than leaving a
    // button that quietly does nothing. A property rather than a field because the builders are instance methods.
    private (string Name, Func<List<NodeBase>> Build, string[]? Fields)[] Tabs => this.tabs ??=
    [
        ("Feet", this.BuildFeet,
        [
            nameof(Settings.Where), nameof(Settings.KeepFeetOutOfWalls), nameof(Settings.MaxRaiseFrac), nameof(Settings.MaxKneeBendDeg),
            nameof(Settings.StraightenLimit), nameof(Settings.MaxAnkleAngleDeg), nameof(Settings.TiltFadeFrac), nameof(Settings.MaxDropFrac),
            nameof(Settings.MaxPelvisRaiseFrac), nameof(Settings.MaxStepDownFrac), nameof(Settings.BlendSeconds), nameof(Settings.PelvisTau),
            nameof(Settings.WallClearanceFrac), nameof(Settings.MaxStepFrac), nameof(Settings.LiftThresholdFrac), nameof(Settings.RayUpFrac),
            nameof(Settings.RayDownFrac), nameof(Settings.RestAdjustFrac), nameof(Settings.SlopeLiftFrac),
        ]),
        ("Edges", this.BuildEdges,
        [
            nameof(Settings.GatherFeet), nameof(Settings.GatherToPosition), nameof(Settings.MinStanceFrac), nameof(Settings.GatherStraighten),
            nameof(Settings.GatherForward), nameof(Settings.GatherPrecision), nameof(Settings.GatherTauIn), nameof(Settings.GatherTauOut),
            nameof(Settings.MaxStanceFrac), nameof(Settings.MaxBodyShiftFrac),
        ]),
        ("Leaning", this.BuildLean,
        [
            nameof(Settings.SlopeLean), nameof(Settings.LeanUphillGain), nameof(Settings.LeanDownhillGain), nameof(Settings.MaxLeanDeg),
            nameof(Settings.MoveWeapons), nameof(Settings.MaxTotalPitchDeg), nameof(Settings.MinTotalPitchDeg), nameof(Settings.LeanTau),
        ]),
        ("Shoving", this.BuildShoving,
        [
            nameof(Settings.Bump), nameof(Settings.Shoved), nameof(Settings.BumpNpcs), nameof(Settings.Grunt), nameof(Settings.BumpCooldown),
            nameof(Settings.BumpSameCooldown), nameof(Settings.BumpRadiusFrac), nameof(Settings.BumpMaxDeg), nameof(Settings.BumpShoveFrac),
            nameof(Settings.BumpHeadHold), nameof(Settings.BumpBodyTurn), nameof(Settings.BumpMinSpeed), nameof(Settings.BumpRiseSeconds),
            nameof(Settings.BumpTau),
        ]),
        ("Emotes", this.BuildEmotes,
        [
            nameof(Settings.Emotes), nameof(Settings.FloorTilt), nameof(Settings.MaxSitTiltDeg), nameof(Settings.SitTiltTau),
        ]),
        ("Performance", this.BuildPerformance,
        [
            nameof(Settings.Others), nameof(Settings.MaxOthers), nameof(Settings.OthersRadius), nameof(Settings.Who),
            nameof(Settings.OthersGatherPrecision), nameof(Settings.MeshRefine), nameof(Settings.MeshRadius), nameof(Settings.MeshBand),
        ]),
        ("Status", this.BuildStatus, null),
    ];

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);

        this.rowWidth = this.ContentSize.X - ScrollBarRoom;

        var bar = new TabBarNode
        {
            Position = this.ContentStartPosition,
            Size = new Vector2(this.ContentSize.X - ResetWidth - Gap, BarHeight),
        };

        for (var i = 0; i < this.Tabs.Length; i++)
        {
            var index = i;
            bar.AddTab(this.Tabs[i].Name, () => this.ShowTab(index));
        }

        // The bar lights its first tab on its own; a window reopened on another tab has to be told.
        bar.SelectTab(this.Tabs[this.tab].Name);
        bar.AttachNode(this);

        this.resetButton = new TextButtonNode
        {
            Position = this.ContentStartPosition + new Vector2(this.ContentSize.X - ResetWidth, 0f),
            Size = new Vector2(ResetWidth, BarHeight),
            String = "Reset tab",
            OnClick = this.AskReset,
        };
        this.resetButton.AttachNode(this);

        this.scroll = new ScrollingNode<VerticalListNode>
        {
            Position = this.ContentStartPosition + new Vector2(0f, BarHeight + Gap),
            Size = this.ContentSize - new Vector2(0f, BarHeight + Gap),
            AutoHideScrollBar = true,
            ContentNode =
            {
                FitContents = true,
                FitWidth = true,
                ItemSpacing = RowGap,
            },
        };
        this.scroll.AttachNode(this);

        this.setup = true;
        this.ShowTab(this.tab);
    }

    protected override unsafe void OnUpdate(AtkUnitBase* addon)
    {
        if (!this.setup)
        {
            return;
        }

        if (this.rebuild)
        {
            this.rebuild = false;
            this.ShowTab(this.tab);
        }

        // Every tab, not just the ones that obviously move: a slider's metres follow the character's leg length and
        // the counters on Performance follow the tick. Only a line whose text actually changed is written.
        this.Refresh();

        if (this.dirty && Environment.TickCount64 - this.dirtyAt > SaveDelayMs)
        {
            this.Flush();
        }
    }

    // The slider component writes its own step count into its value node from the addon's Update, which runs after
    // OnUpdate. Draw is the next callback after that, so writing the reading we want here is what puts ours last.
    protected override unsafe void OnDraw(AtkUnitBase* addon)
    {
        if (!this.setup)
        {
            return;
        }

        foreach (var (node, text) in this.forced)
        {
            node.String = text();
        }
    }

    // Nothing may touch a node after this: the game tears the tree down between hiding the window and finalising
    // it, and both callbacks keep running in between.
    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        this.setup = false;
        this.Flush();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        this.Flush();
        this.setup = false;
        this.live.Clear();
        this.gates.Clear();
        this.forced.Clear();
        this.scroll = null;
        this.resetButton = null;
    }

    // KamiToolKit loads its textures before any game node can exist, so the window is built from here rather than
    // from the plugin's constructor.
    internal static async Task InstallAsync(Plugin owner, CancellationToken token)
    {
        await KamiToolKitLibrary.InitializeAsync(Plugin.PluginInterface, "Inverse Kinematics");

        // An unload asked for during the load has already taken the handlers off by the time we get here, and
        // registering them again would leave a command and a draw hook behind a plugin that is on its way out.
        token.ThrowIfCancellationRequested();

        owner.AttachWindow(new Overlay
        {
            Owner = owner,
            InternalName = "FootIkSettings",
            Title = "Inverse Kinematics",
            Size = new Vector2(820f, 620f),
        });
    }

    // The other half of the same arrangement: Dalamud calls DisposeAsync off the framework thread, so the hooks
    // and our own offsets have to be put back from a call marshalled onto it.
    internal static async ValueTask ShutdownAsync(Plugin owner, Overlay? window)
    {
        try
        {
            await Plugin.Framework.RunOnFrameworkThread(owner.Teardown);
        }
        catch (Exception ex)
        {
            // Whatever the hooks and the offsets did, the window still has to come down: native nodes left attached
            // outlive the plugin that owns them.
            Plugin.Log.Error(ex, "FootIk: teardown failed");
        }

        // That await resumes on the framework thread, and NativeAddon.CloseAsync refuses to run there: it waits out
        // the window's closing animation, which needs frames to pass, so waiting from the thread that draws them
        // would never finish. Task.Run puts the rest back on the pool.
        await Task.Run(async () =>
        {
            if (window is not null)
            {
                if (window.confirm is not null)
                {
                    await window.confirm.DisposeAsync();
                    window.confirm = null;
                }

                await window.DisposeAsync();
            }

            await KamiToolKitLibrary.DisposeAsync();
        });
    }

    // Settings save when an edit settles rather than on every step of a drag: SavePluginConfig writes the file
    // synchronously on the draw thread.
    private void Touch()
    {
        this.dirty = true;
        this.dirtyAt = Environment.TickCount64;
        this.Refresh();
    }

    private void Flush()
    {
        if (this.dirty)
        {
            this.dirty = false;
            this.Owner.SaveSettings();
        }
    }

    private void Refresh()
    {
        var resized = false;
        foreach (ref var line in CollectionsMarshal.AsSpan(this.live))
        {
            var text = line.Text();
            if (text == line.Shown)
            {
                continue;
            }

            line.Shown = text;
            line.Node.String = text;

            // Visibility is left alone here, whatever the line says. A collapsing header hides its contents by
            // turning them off one by one, so a readout inside a collapsed fold that showed itself again on the
            // strength of having text would come back without the fold it belongs to, and the list would be
            // re-flowed around a widget nobody can see. An empty line holds a blank row open instead.
            resized |= Fit(line.Node);
        }

        // A gate that has not moved is left alone: some of them hide a widget outright, and the list around it has
        // to be re-flowed when they do.
        foreach (ref var gate in CollectionsMarshal.AsSpan(this.gates))
        {
            var on = gate.On();
            if (gate.Applied == on)
            {
                continue;
            }

            gate.Applied = on;
            gate.Set(on);
            resized = true;
        }

        if (resized)
        {
            this.Relayout();
        }
    }

    // How many lines a wrapped string takes is the font's business, not ours, and a node left too short does not
    // clip: the overflow is drawn straight over whatever the list put underneath it. Ask the game how wide the
    // text draws and keep the node as tall as the rows it wraps into.
    private static bool Fit(TextNode node)
    {
        if (!node.TextFlags.HasFlag(TextFlags.WordWrap))
        {
            return false;
        }

        var drawn = node.GetTextDrawSize();
        var wrapped = MathF.Ceiling(drawn.X / MathF.Max(1f, node.Width)) * LineHeight;
        var height = MathF.Max(LineHeight, MathF.Max(drawn.Y, wrapped));
        if (MathF.Abs(node.Height - height) < 0.5f)
        {
            return false;
        }

        node.Height = height;
        return true;
    }

    private TextNode Watch(TextNode node, Func<string> text)
    {
        this.live.Add((node, text, string.Empty));
        return node;
    }

    private void Gate(Func<bool> on, Action<bool> set) => this.gates.Add((on, set, null));

    // The reset window outlives this one and can be confirmed after it has been closed, so a rebuild has to be
    // refused while the tree is down: the nodes are already gone between the hide and the finalise.
    private void ShowTab(int index)
    {
        if (!this.setup)
        {
            return;
        }

        this.tab = index;
        this.live.Clear();
        this.gates.Clear();
        this.forced.Clear();

        if (this.scroll is null)
        {
            return;
        }

        this.scroll.ContentNode.Clear();
        this.scroll.ContentNode.AddNode(this.Tabs[index].Build());

        // Text before layout, everywhere: a node's height follows the string it carries.
        this.Refresh();
        this.scroll.ScrollToStart();
        this.scroll.RecalculateSizes();

        if (this.resetButton is not null)
        {
            this.resetButton.String = this.Tabs[index].Fields is null ? "Reset all" : "Reset tab";
        }
    }

    private void Relayout()
    {
        if (this.scroll is null)
        {
            return;
        }

        // The content's own height has to be settled before the scroll node reads it, or the bar sizes itself
        // against the list as it was. Moving the content by hand from here is what scattered the tab: the scroll
        // bar holds the offset, and putting the node somewhere it did not put it leaves the two disagreeing.
        this.scroll.ContentNode.RecalculateLayout();
        this.scroll.RecalculateSizes();
    }

    private void AskReset()
    {
        // Both taken now: the window stays usable while the question is up, so the tab may have moved on by the
        // time it is answered, and the answer belongs to the tab that asked.
        var index = this.tab;
        var fields = this.Tabs[index].Fields;
        var global = fields is null;

        this.confirm ??= new ConfirmWindow
        {
            InternalName = "FootIkReset",
            Title = "Reset settings",
            Size = new Vector2(400f, 180f),
        };

        this.confirm.Prompt = global
            ? "Put every setting, on every tab, back to how it shipped?"
            : "Put the settings on this tab back to how they shipped? The other tabs are left alone.";
        this.confirm.Label = global ? "Reset everything" : "Reset this tab";
        this.confirm.OnConfirm = () =>
        {
            this.Owner.Settings.Reset(fields);
            this.Owner.SaveSettings();
            this.ShowTab(index);
        };

        this.confirm.Open();
    }

    // A text node left free to resize itself rewrites its own width on every SetText, and the row around it was
    // laid out before that: the value beside a slider ends up walked left over the slider's label. Ellipsis
    // instead, so a node keeps the width its row gave it however long the string turns out to be.
    private TextNode Label(string text, float width, bool dim = false)
    {
        var node = new TextNode
        {
            Height = TextHeight,
            AlignmentType = AlignmentType.Left,
            TextColor = dim ? Dim : ColorHelper.GetColor(8u),
        };

        node.RemoveTextFlags(TextFlags.AutoAdjustNodeSize);
        node.AddTextFlags(TextFlags.Ellipsis);
        node.String = text;
        node.Width = width;
        return node;
    }

    // Wrapped text starts one line tall and is measured by Fit on its first write, so nothing here guesses at
    // where a string breaks. Its width is fixed for the same reason a Label's is.
    private TextNode Wrap(bool dim)
    {
        var node = new TextNode
        {
            Height = LineHeight,
            AlignmentType = AlignmentType.TopLeft,
            LineSpacing = (uint)LineHeight,
            TextColor = dim ? Dim : ColorHelper.GetColor(8u),
        };

        node.RemoveTextFlags(TextFlags.AutoAdjustNodeSize);
        node.AddTextFlags(TextFlags.WordWrap, TextFlags.MultiLine);
        node.Width = this.rowWidth;
        return node;
    }

    private NodeBase Note(string text) => this.Watch(this.Wrap(true), () => text);

    private NodeBase Section(string text) => new UnderlinedTextNode
    {
        Height = BarHeight,
        Width = this.rowWidth,
        String = text,
    };

    // Built on the first uncollapse: the diagnostic folds hold well over a hundred text nodes that most people
    // never open.
    // Folding rebuilds the tab from scratch on the next frame rather than re-flowing what is already there. A
    // collapsing header shrinks without telling the list above it, and re-flowing that list afterwards left
    // settings drawn on top of each other and whole rows pushed off the window; building a tab is the one path
    // known to lay out correctly, so folding uses it. Next frame, because the rebuild disposes the very header
    // whose click is still on the stack.
    private CollapsingHeaderNode Fold(string label, Func<IEnumerable<NodeBase>> build)
    {
        var key = $"{this.tab}:{label}";
        var open = this.unfolded.Contains(key);

        var header = new CollapsingHeaderNode
        {
            Width = this.rowWidth,
            FitWidth = true,
            ItemSpacing = RowGap,
            IsCollapsed = !open,
            String = label,
        };

        if (open)
        {
            header.AddNode(build());
        }

        header.OnToggle = nowOpen =>
        {
            if (nowOpen)
            {
                this.unfolded.Add(key);
            }
            else
            {
                this.unfolded.Remove(key);
            }

            this.rebuild = true;
        };

        return header;
    }

    private NodeBase Check(string label, Func<bool> get, Action<bool> set, string help, Func<bool>? enabled = null)
    {
        var box = new CheckboxNode
        {
            Height = RowHeight,
            Width = this.rowWidth,
            String = label,
            TextTooltip = help,
            IsChecked = get(),
        };

        box.OnClick = value =>
        {
            set(value);
            this.Touch();
        };

        // A setting can be turned off behind the window's back — the mesh scan does it to itself when it faults —
        // and a tick left standing would say otherwise.
        this.Gate(get, on => box.IsChecked = on);

        if (enabled is not null)
        {
            this.Gate(enabled, on =>
            {
                box.IsEnabled = on;
                box.Alpha = on ? 1f : 0.5f;
            });
        }

        return box;
    }

    // A setting reads as its name and current value on one line with the control under them, rather than a name
    // squeezed down the left of every row.
    // No FitWidth: a slider is deliberately short of the full row, leaving room for the value text that hangs off
    // its right end, and stretching it to fit would push that text off the window.
    private NodeBase Stack(NodeBase heading, NodeBase control) => new VerticalListNode
    {
        Width = this.rowWidth,
        FitContents = true,
        ItemSpacing = 2f,
        InitialNodes = [heading, control],
    };

    // The game's slider is integer-backed, so every setting is carried as a whole number of steps above its own
    // minimum: that keeps a 0.001 setting exact and a negative minimum expressible.
    private NodeBase Slide(string label, Func<float> get, Action<float> set, float min, float max, float scale, Func<float, string> format, string help, Func<bool>? enabled = null)
    {
        var steps = (int)MathF.Round((max - min) * scale);
        var slider = new SliderNode
        {
            Height = RowHeight,
            Width = this.rowWidth - ValueWidth - Gap,
            Range = 0..steps,
            Step = 1,
            Value = Math.Clamp((int)MathF.Round((get() - min) * scale), 0, steps),
        };

        slider.OnValueChanged = value =>
        {
            set(min + (value / scale));
            this.Touch();
        };

        // The component's own value text, left where it puts it — past the end of the track, which is where the
        // game shows a slider's reading — but carrying the setting rather than the step count. Alignment is ours
        // to set because OnSizeChanged only rewrites size and position; the width looks after itself, the node
        // being flagged to size to its text. `ValueWidth` is the room the slider gives up to leave it somewhere.
        slider.ValueNode.AlignmentType = AlignmentType.Left;
        this.forced.Add((slider.ValueNode, () => format(get())));

        var name = this.Label(label, this.rowWidth);
        name.TextTooltip = help;

        if (enabled is not null)
        {
            // The slider dims its own value text through its timeline, so only the name needs a hand.
            this.Gate(enabled, on =>
            {
                slider.IsEnabled = on;
                name.Alpha = on ? 1f : 0.5f;
            });
        }

        return this.Stack(name, slider);
    }


    private NodeBase SlideInt(string label, Func<int> get, Action<int> set, int min, int max, Func<int, string> format, string help, Func<bool>? enabled = null) =>
        this.Slide(label, () => get(), v => set((int)MathF.Round(v)), min, max, 1f, v => format((int)MathF.Round(v)), help, enabled);

    private NodeBase Pick<T>(string label, Func<T> get, Action<T> set, string help, Func<bool>? enabled = null) where T : struct, Enum
    {
        var drop = new EnumDropDownNode<T>
        {
            Height = RowHeight,
            Width = this.rowWidth,
            MaxListOptions = Enum.GetValues<T>().Length,
            Options = [.. Enum.GetValues<T>()],
            SelectedOption = get(),
        };

        drop.OnOptionSelected = value =>
        {
            set(value);
            this.Touch();
        };

        var name = this.Label(label, this.rowWidth);
        name.TextTooltip = help;

        if (enabled is not null)
        {
            this.Gate(enabled, on =>
            {
                drop.IsEnabled = on;
                name.Alpha = on ? 1f : 0.5f;
            });
        }

        return this.Stack(name, drop);
    }

    private NodeBase Readout(Func<string> text) => this.Watch(this.Wrap(false), text);

    // The row is as tall as the labels in it: a shorter row and the list walks the next one up over this one.
    private NodeBase Columns(string name, Func<string> left, Func<string> right)
    {
        var width = (this.rowWidth - LabelWidth - (Gap * 2f)) / 2f;
        var l = this.Label(string.Empty, width);
        var r = this.Label(string.Empty, width);
        this.Watch(l, left);
        this.Watch(r, right);

        return new HorizontalListNode
        {
            Height = RowHeight,
            Width = this.rowWidth,
            ItemSpacing = Gap,
            InitialNodes = [this.Label(name, LabelWidth), l, r],
        };
    }

    private NodeBase ColumnHeads()
    {
        var width = (this.rowWidth - LabelWidth - (Gap * 2f)) / 2f;
        return new HorizontalListNode
        {
            Height = RowHeight,
            Width = this.rowWidth,
            ItemSpacing = Gap,
            InitialNodes = [this.Label(" ", LabelWidth), this.Label("Left", width, true), this.Label("Right", width, true)],
        };
    }

    private List<NodeBase> BuildFeet()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Pick("Enabled", () => c.Where, v => c.Where = v,
                "Where the plugin places feet. Group pose is off by default: posing tools such as Brio and Ktisis move the same bones, and the two will fight over them."),
            this.Check("Keep feet out of walls", () => c.KeepFeetOutOfWalls, v => c.KeepFeetOutOfWalls = v,
                "Stops the feet from clipping into walls, kerbs and steps by moving them slightly aside. Works best when feet gathering is enabled."),
            this.Fold("Advanced", () =>
            [
                this.Section("Feet/leg placement settings"),
                this.Slide("Max foot raise", () => c.MaxRaiseFrac, v => c.MaxRaiseFrac = v, 0f, 0.8f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far a foot may be lifted up. A foot that has to rise above this value will be underground. High values may cause unnatural poses."),
                this.Slide("Max knee bend", () => c.MaxKneeBendDeg, v => c.MaxKneeBendDeg = v, 30f, 150f, 1f, v => $"{v:F0} deg",
                    "How far the knee may bend. Values that are too low may cause the highest foot to clip into the floor. Values that are too high may cause unnatural poses."),
                this.Slide("Straighten limit", () => c.StraightenLimit, v => c.StraightenLimit = v, 0.90f, 0.999f, 1000f, v => $"{v:F3}",
                    "How far a leg can extend to reach its target. Helps races with hunched backs (e.g. male Hrothgar) more naturally place their feet."),
                this.Slide("Max tilt", () => c.MaxAnkleAngleDeg, v => c.MaxAnkleAngleDeg = v, 0f, 60f, 1f, v => $"{v:F0} deg",
                    "How far the foot may rotate to match the surface under it. Zero turns ankle alignment off."),
                this.Slide("Tilt fade", () => c.TiltFadeFrac, v => c.TiltFadeFrac = v, 0.01f, 0.3f, 100f, v => $"{v:F2}  ({this.Metres(v, 3)} m)",
                    "Tilt fades out over this height above resting height, so a lifting or heel-striking foot keeps the pose the animation gave it."),
                this.Section("Body placement settings"),
                this.Slide("Max body drop", () => c.MaxDropFrac, v => c.MaxDropFrac = v, 0f, 0.8f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far the body may sink toward a foot standing lower than the character. Low values may cause the lowest foot to hover above the ground. High values may cause unnatural poses."),
                this.Slide("Max body rise", () => c.MaxPelvisRaiseFrac, v => c.MaxPelvisRaiseFrac = v, 0f, 0.4f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far the body may rise when a foot stands higher than the character. Low values may cause the highest leg to bend too much. High values may cause unnatural poses."),
                this.Slide("Edge drop", () => c.MaxStepDownFrac, v => c.MaxStepDownFrac = v, 0.05f, 0.8f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far below the character a foot will reach for the ground. A foot over a bigger drop than this stays in place instead of stretching down."),
                this.Slide("Blend in/out", () => c.BlendSeconds, v => c.BlendSeconds = v, 0f, 1f, 100f, v => $"{v:F2} s",
                    "Fade time when the effect turns on or off, such as mounting or entering a cutscene."),
                this.Slide("Drop smoothing", () => c.PelvisTau, v => c.PelvisTau = v, 0.01f, 0.5f, 100f, v => $"{v:F2} s",
                    "Smoothing on the body height. Higher values will be smoother but might react late to floor height changes."),
                this.Section("Keep feet out of walls"),
                this.Slide("Foot width", () => c.WallClearanceFrac, v => c.WallClearanceFrac = v, 0f, 0.2f, 100f, v => $"{v:F2}  ({this.Metres(v * 2f)} m wide)",
                    "How wide the feet are considered to be when avoiding walls. Increase if the feet still clip into walls. Decrease if they stay too far away from them.",
                    () => c.KeepFeetOutOfWalls),
                this.Section("Collision detection settings"),
                this.Slide("Ignore ground beyond", () => c.MaxStepFrac, v => c.MaxStepFrac = v, 0.1f, 1.2f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "Ground further than this from standing height is not treated as a surface for that foot."),
                this.Slide("Planted threshold", () => c.LiftThresholdFrac, v => c.LiftThresholdFrac = v, 0.02f, 0.6f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "A foot within this height of its resting height carries full weight; weight fades to none at twice the distance."),
                this.Slide("Probe start", () => c.RayUpFrac, v => c.RayUpFrac = v, 0.1f, 2f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far above standing height the mod checks for floors. Must be high enough to correctly detect floors."),
                this.Slide("Probe reach", () => c.RayDownFrac, v => c.RayDownFrac = v, 0.1f, 3f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far down the mod checks for ground before reporting nothing there."),
                this.Section("Adjustments"),
                this.Slide("Rest adjust", () => c.RestAdjustFrac, v => c.RestAdjustFrac = v, -0.1f, 0.1f, 1000f, v => $"{v:F3}  ({this.Metres(v, 3)} m)",
                    "Manual correction to the base height of the feet. Change when the feet always seem misplaced."),
                this.Slide("Slope lift", () => c.SlopeLiftFrac, v => c.SlopeLiftFrac = v, 0f, 0.5f, 1000f, v => $"{v:F3}  ({this.Metres(v, 3)} m)",
                    "Extra foot raise proportional to how steep the ground is. Workaround to feet clipping slightly into the ground when standing on slopes."),
            ]),
        ];
    }

    private List<NodeBase> BuildEdges()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Check("Gather feet", () => c.GatherFeet, v => c.GatherFeet = v,
                "Move your character's feet together when standing on narrow platforms to avoid them floating over an edge."),
            this.Check("Platforming mode", () => c.GatherToPosition, v => c.GatherToPosition = v,
                "Enabling this prioritises feet positions that avoid sliding your character off-centre. This helps better seeing where your character really is, at the cost of worse poses.",
                () => c.GatherFeet),
            this.SlideInt("Precision", () => c.GatherPrecision, v => c.GatherPrecision = v, 1, 4, v => $"{v}  ({4 * v} directions)",
                "How carefully the plugin looks around each foot for ground to stand on. Higher finds narrow rails and beams more reliably and keeps the feet steadier, but costs more each frame. Lower it if the game slows down near edges.",
                () => c.GatherFeet),
            this.Fold("Advanced", () =>
            [
                this.Slide("Min stance", () => c.MinStanceFrac, v => c.MinStanceFrac = v, 0.02f, 0.4f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How close the feet can be of each other when gathering. Avoids the feet crossing or overlapping each other.", () => c.GatherFeet),
                this.Slide("Max stance", () => c.MaxStanceFrac, v => c.MaxStanceFrac = v, 0.5f, 4f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far apart the feet may end up when gathering. Wider than this and the plugin leaves the feet alone for that frame, so a bad reading never splays the legs.", () => c.GatherFeet),
                this.Slide("Max body move", () => c.MaxBodyShiftFrac, v => c.MaxBodyShiftFrac = v, 0.25f, 3f, 100f, v => $"{v:F2}  ({this.Metres(v)} m)",
                    "How far gathering may move your character's body onto the feet. Further than this and the plugin leaves the feet alone for that frame, so a bad reading never throws your character across the room.", () => c.GatherFeet),
                this.Slide("Straighten legs", () => c.GatherStraighten, v => c.GatherStraighten = v, 0f, 1f, 100f, v => $"{v:F2}",
                    "How much the legs straighten when the feet are gathered. Avoids legs being flexed while gathered on some races.", () => c.GatherFeet),
                this.Slide("Feet forward", () => c.GatherForward, v => c.GatherForward = v, 0f, 1f, 100f, v => $"{v:F2}",
                    "How strongly the knees and feet turn to face forward when gathered. Avoids duck feet on some races.", () => c.GatherFeet),
                this.Slide("Ease in", () => c.GatherTauIn, v => c.GatherTauIn = v, 0.05f, 1f, 100f, v => $"{v:F2} s",
                    "How quickly the feet move in onto a support.", () => c.GatherFeet),
                this.Slide("Ease out", () => c.GatherTauOut, v => c.GatherTauOut = v, 0.05f, 1f, 100f, v => $"{v:F2} s",
                    "How quickly they return when stepping off.", () => c.GatherFeet),
            ]),
        ];
    }

    private List<NodeBase> BuildLean()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Check("Lean into slopes", () => c.SlopeLean, v => c.SlopeLean = v,
                "While moving, makes the character lean forwards or backwards when running uphill and downhill."),
            this.Note("Balance against the slope while running."),
            this.Slide("Uphill gain", () => c.LeanUphillGain, v => c.LeanUphillGain = v, 0f, 2f, 100f, v => $"{v:F2}",
                "How strongly the character leans forwards when running uphill, at full run speed.", () => c.SlopeLean),
            this.Slide("Downhill gain", () => c.LeanDownhillGain, v => c.LeanDownhillGain = v, 0f, 2f, 100f, v => $"{v:F2}",
                "How far the character leans back when running downhill.", () => c.SlopeLean),
            this.Slide("Max lean", () => c.MaxLeanDeg, v => c.MaxLeanDeg = v, 0f, 45f, 1f, v => $"{v:F0} deg",
                "How far the character can lean in either direction.", () => c.SlopeLean),
            this.Section("Carried weapons"),
            this.Check("Weapons follow the body", () => c.MoveWeapons, v => c.MoveWeapons = v,
                "Sheathed weapons and shields ride along when the upper body leans, turns or tilts, instead of staying where the animation left them. Turn it off if a weapon ends up floating."),
            this.Fold("Advanced", () =>
            [
                this.Slide("Max spine pitch", () => c.MaxTotalPitchDeg, v => c.MaxTotalPitchDeg = v, 10f, 90f, 1f, v => $"{v:F0} deg",
                    "Limits how much the character may lean while taking account their base animations. Avoids hunched races lean forwards too much."),
                this.Slide("Min spine pitch", () => c.MinTotalPitchDeg, v => c.MinTotalPitchDeg = v, -45f, 0f, 1f, v => $"{v:F0} deg",
                    "Limits how far back the character may lean, taking account of their base animation. Avoids upright races arching backwards when running downhill."),
                this.Slide("Lean smoothing", () => c.LeanTau, v => c.LeanTau = v, 0.05f, 1f, 100f, v => $"{v:F2} s",
                    "Smoothing on the lean angle, so a sudden change in terrain does not snap the torso."),
            ]),
        ];
    }

    private List<NodeBase> BuildShoving()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Check("Flinch when bumping into someone", () => c.Bump, v => c.Bump = v,
                "Allows you to shove people when running into them. The shove depends on the speed and angle of impact, the difference in height of both people, and other factors."),
            this.Check("Bump into NPCs", () => c.BumpNpcs, v => c.BumpNpcs = v,
                "Whether NPCs count. Off, only players can bump you or be bumped. This is separate from who gets their feet placed on the Performance tab.", () => c.Bump),
            this.Check("Let others shove you", () => c.Shoved, v => c.Shoved = v,
                "Other players running or walking into you shove you as well.", () => c.Bump),
            this.Note("They flinch back only when \"Other players\" or \"Everyone\" is set in the performance settings."),
            this.Check("Grunt when shoved", () => c.Grunt, v => c.Grunt = v,
                "Make both characters grunt when they one or the other is shoved. Has a random chance of playing a grunt.", () => c.Bump),
            this.Section("Cooldowns"),
            this.Slide("Bump cooldown", () => c.BumpCooldown, v => c.BumpCooldown = v, 0f, 3f, 10f, v => $"{v:F1} s",
                "The least time between two bumps, whoever they are with. Raise it if a crowd keeps your character flinching.", () => c.Bump),
            this.Slide("Same character", () => c.BumpSameCooldown, v => c.BumpSameCooldown = v, 0f, 10f, 10f, v => $"{v:F1} s",
                "How long before running into the same character again counts as a new bump.", () => c.Bump),
            this.Fold("Advanced", () =>
            [
                this.Slide("Bump distance", () => c.BumpRadiusFrac, v => c.BumpRadiusFrac = v, 0.1f, 0.8f, 100f, v => $"{v:F2}  ({this.Metres(v * 2f)} m apart)",
                    "How close two characters must come to count as touching. Too low and you pass through people without a reaction; too high and you flinch at people you clearly missed.", () => c.Bump),
                this.Slide("Bump strength", () => c.BumpMaxDeg, v => c.BumpMaxDeg = v, 0f, 60f, 1f, v => $"{v:F0} deg",
                    "How far the upper body tips away and turns towards the other character at the moment of impact.", () => c.Bump),
                this.Slide("Give way", () => c.BumpShoveFrac, v => c.BumpShoveFrac = v, 0f, 0.5f, 100f, v => $"{v:F2}  ({v * this.Owner.Snap.LegLength * 100f:F0} cm)",
                    "How far the hips are pushed off the feet, which is what bends the knees and makes a standing character look shoved rather than folded at the waist. Too high and they sink into a crouch.", () => c.Bump),
                this.Slide("Keep looking ahead", () => c.BumpHeadHold, v => c.BumpHeadHold = v, 0f, 1f, 100f, v => $"{v:F2}",
                    "How much the neck holds the head still while the body is shoved out from under it. Full keeps the character looking where it was looking; zero lets the head ride round with the shoulders.", () => c.Bump),
                this.Slide("Whole-body turn", () => c.BumpBodyTurn, v => c.BumpBodyTurn = v, 0f, 1.5f, 100f, v => $"{v:F2}",
                    "How much of the turn the whole body takes at running speed, hips and all, on top of the shoulders; the legs keep following the stride. Zero keeps every bump in the upper body; standing characters always do.", () => c.Bump),
                this.Slide("Bump speed", () => c.BumpMinSpeed, v => c.BumpMinSpeed = v, 0.2f, 10f, 10f, v => $"{v:F1} m/s",
                    "How fast the two of you must be closing on each other for it to count as a bump. Set it above walking speed and only running bumps; set it low and brushing past someone jolts you.", () => c.Bump),
                this.Slide("Bump rise", () => c.BumpRiseSeconds, v => c.BumpRiseSeconds = v, 0.02f, 0.3f, 100f, v => $"{v:F2} s",
                    "How quickly the body reaches its full flinch after the impact.", () => c.Bump),
                this.Slide("Bump recovery", () => c.BumpTau, v => c.BumpTau = v, 0.1f, 1.5f, 100f, v => $"{v:F2} s",
                    "How long the body takes to straighten up again.", () => c.Bump),
            ]),
        ];
    }

    private List<NodeBase> BuildEmotes()
    {
        var c = this.Owner.Settings;
        return
        [
            this.Check("Work during emotes", () => c.Emotes, v => c.Emotes = v,
                "Keeps placing the feet through dances and other looping emotes, and settles the body onto the slope when sitting or sleeping on the ground."),
            this.Check("Tilt lying-down poses too", () => c.FloorTilt, v => c.FloorTilt = v,
                "Turns the body to follow the slope when lying on the ground, not just when sitting. Affects emotes like pushups and playing dead. Turn it off if the tilt looks worse than leaving the animation alone.",
                () => c.Emotes),
            this.Slide("Max sit tilt", () => c.MaxSitTiltDeg, v => c.MaxSitTiltDeg = v, 0f, 45f, 1f, v => $"{v:F0} deg",
                "How far the body may tilt to rest on sloped ground when sitting or sleeping on it. Zero keeps the body upright.", () => c.Emotes),
            this.Slide("Sit tilt smoothing", () => c.SitTiltTau, v => c.SitTiltTau = v, 0.05f, 1.5f, 100f, v => $"{v:F2} s",
                "How quickly the body settles onto the slope when sitting down, and comes back up when standing.", () => c.Emotes),
        ];
    }

    private List<NodeBase> BuildPerformance()
    {
        var c = this.Owner.Settings;
        var p = this.Owner;
        return
        [
            this.Check("Apply on other characters", () => c.Others, v => c.Others = v,
                "Applies IK to other players and NPCs around you. Each one costs FPS. Tweak the settings if the game stutters too much."),
            this.SlideInt("Max characters", () => c.MaxOthers, v => c.MaxOthers = v, 1, 50, v => $"{v}",
                "How many characters to apply IK to. Lower this if your frame rate drops in crowds.", () => c.Others),
            this.Slide("Max distance", () => c.OthersRadius, v => c.OthersRadius = v, 3f, 50f, 1f, v => $"{v:F0} yalms",
                "How far away a character can be and still have its feet placed.", () => c.Others),
            this.Pick("Apply IK to", () => c.Who, v => c.Who = v,
                "Which characters around you have IK applied. \"Everyone\" includes NPCs.", () => c.Others),
            this.SlideInt("Gather feet precision", () => c.OthersGatherPrecision, v => c.OthersGatherPrecision = v, 0, 4,
                v => v == 0 ? "off" : $"{v}  ({4 * v} directions)",
                "Whether other characters also get their feet gathered onto narrow ledges and rails, and how carefully. Needs Gather feet on the Edges tab.",
                () => c.Others && c.GatherFeet),
            this.Check("Use higher precision collision", () => c.MeshRefine, v => c.MeshRefine = v,
                "Places feet on the ground you actually see instead of the simplified shape the game uses for walking. Fixes stairs that otherwise behave like a ramp. Uses some memory, and a little frame time while a new area loads."),
            this.Fold("Advanced", () =>
            [
                this.Slide("Read distance", () => c.MeshRadius, v => c.MeshRadius = v, 10f, 60f, 1f, v => $"{v:F0} yalms",
                    "How far around you the visible ground is read. Keep it at least as far as Max distance above, or far-away characters fall back to the simple shape.", () => c.MeshRefine),
                this.Slide("Max difference", () => c.MeshBand, v => c.MeshBand = v, 0.05f, 1f, 100f, v => $"{v:F2} m",
                    "How far the visible ground may be from the simple shape and still be believed. Too low and stairs are missed; too high and a foot may land on furniture.", () => c.MeshRefine),
            ]),
            // The scan switches its own setting off when it faults, so the reason it did has to be somewhere.
            this.Readout(() => p.MeshStatus == "off" ? string.Empty : $"Higher precision collision: {p.MeshStatus}"),
            this.Readout(() => $"Working on {p.Tracked} character{(p.Tracked == 1 ? string.Empty : "s")}."),
            this.Readout(() => $"Frame cost {p.LastMicros:F0} us, max {p.MaxMicros:F0} us."),
        ];
    }

    private List<NodeBase> BuildStatus()
    {
        var p = this.Owner;
        var c = p.Settings;

        var rearm = new TextButtonNode
        {
            Height = BarHeight,
            Width = 140f,
            String = "Start again",
            OnClick = p.Rearm,
        };
        // Nothing to re-arm until the fault breaker has actually tripped, and a button that is disabled forever
        // only invites the question of what it is for. It leaves the list instead.
        this.Gate(() => p.Tripped, on => rearm.IsVisible = on);

        return
        [
            this.Readout(() => p.Tripped
                ? $"Stopped after {p.Faults} errors. Everyone is back at the game's own height."
                : Hooked(p) ? "Running." : "Not running. See Details below."),
            rearm,
            this.Readout(() => p.LastError is null ? string.Empty : $"Last error: {p.LastError}"),
            this.Readout(() => $"Frame cost {p.LastMicros:F0} us, peak {p.MaxMicros:F0} us"),
            this.Check("Show markers in the world", () => c.ShowMarkers, v => c.ShowMarkers = v, "Draws a dot at each ankle and the ground under it."),
            this.Check("Show a one-metre ruler at your feet", () => c.ShowRuler, v => c.ShowRuler = v, "Draws a metre along the ground and a metre up, ticked every ten centimetres."),
            this.Section("Your character"),
            this.Readout(() => Activity(in p.Snap, c.Where)),
            this.Readout(() => $"Moving at {p.Snap.Speed:F1} m/s"),
            this.Readout(() => $"Body height {Cm(p.Snap.Applied)}   lean {p.Snap.LeanDeg:F0} deg   bump {p.Snap.BumpDeg:F0} deg   tilt to the ground {p.Snap.SitTiltDeg:F0} deg"),
            this.Section("Feet"),
            this.ColumnHeads(),
            this.Columns("standing", () => State(in p.Snap.Left), () => State(in p.Snap.Right)),
            this.Columns("planted", () => $"{p.Snap.Left.Planted:P0}", () => $"{p.Snap.Right.Planted:P0}"),
            this.Columns("height", () => Cm(p.Snap.Left.Offset), () => Cm(p.Snap.Right.Offset)),
            this.Columns("tilt", () => $"{p.Snap.Left.TiltDeg:F0} deg", () => $"{p.Snap.Right.TiltDeg:F0} deg"),
            this.Columns("moved", () => Cm((p.Snap.Left.GatherShift + p.Snap.Left.WallShift).Length()), () => Cm((p.Snap.Right.GatherShift + p.Snap.Right.WallShift).Length())),
            this.Fold("Details", this.BuildDetails),
        ];
    }

    private List<NodeBase> BuildDetails()
    {
        var p = this.Owner;

        var peaks = new TextButtonNode
        {
            Height = BarHeight,
            Width = 140f,
            String = "Reset peaks",
            OnClick = () =>
            {
                this.rawPeak = 0f;
                this.lostPeak = 0f;
            },
        };

        return
        [
            this.Readout(() => $"Render hook: {p.HookStatus}"),
            this.Readout(() => $"Move hook: {p.MoveHookStatus}"),
            this.Readout(() => $"Solver self-test: {p.SelfTest}"),
            this.Readout(() => $"Faults: {p.Faults}  {(p.Tripped ? "TRIPPED" : "armed")}"),
            this.Readout(() => $"Gate {p.Snap.Gate}   blend {p.Snap.Blend:F2}   speed {p.Snap.Speed:F2} m/s"),
            this.Readout(() => $"Mode {p.Snap.Mode} ({p.Snap.ModeParam})   jumping {p.Snap.IsJumping}   gpose {p.Snap.GPose}   blocking condition {p.Snap.Conditions}"),
            // Peaks decay slowly so a value that only exists for a frame or two while walking can still be read off.
            this.Readout(() =>
            {
                this.rawPeak = MathF.Max(this.rawPeak * 0.99f, MathF.Abs(p.Snap.RawDrop));
                return $"Body drop raw {p.Snap.RawDrop:F3}  smooth {p.Snap.SmoothDrop:F3}  applied {p.Snap.Applied:F3}  peak {this.rawPeak:F3}";
            }),
            this.Readout(() =>
            {
                this.lostPeak = MathF.Max(this.lostPeak * 0.99f, MathF.Abs(p.Snap.OffsetSeen - p.Snap.OffsetWritten));
                return $"Draw offset ours {p.Snap.OffsetWritten:F3}  game holds {p.Snap.OffsetSeen:F3}  lost peak {this.lostPeak:F3}";
            }),
            peaks,
            this.Readout(() => $"Ground under body {p.Snap.BaseY:F3}   body shift {Fmt(p.Snap.BodyShift)}"),
            this.Readout(() => $"Spine pitch {p.Snap.SpinePitchDeg:F1} deg   lean {p.Snap.LeanDeg:F1} deg   body height {p.Snap.Height:F2}   weapons following {p.Snap.Weapons}"),
            this.Readout(() => $"On the floor {p.Snap.OnFloor}   hips {p.Snap.HipFrac:F2}   body tilt {p.Snap.SitTiltDeg:F1} deg"),
            this.Readout(() => p.Snap.HasPose && !p.Snap.ChainResolved ? "Chain: not resolved (j_asi_[a,b,d,e]_[lr] missing)" : string.Empty),
            this.ColumnHeads(),
            this.Columns("ground hit", () => YesNo(p.Snap.Left.Hit), () => YesNo(p.Snap.Right.Hit)),
            this.Columns("ground Y", () => $"{p.Snap.Left.GroundModelY:F3}", () => $"{p.Snap.Right.GroundModelY:F3}"),
            this.Columns("ankle above ground", () => $"{p.Snap.Left.AnkleAboveGround:F3}", () => $"{p.Snap.Right.AnkleAboveGround:F3}"),
            this.Columns("rest (bind)", () => $"{p.Snap.Left.Rest:F3}", () => $"{p.Snap.Right.Rest:F3}"),
            this.Columns("delta",
                () => $"{(p.Snap.Left.Hit ? p.Snap.Left.Rest - p.Snap.Left.AnkleAboveGround : 0f):F3}",
                () => $"{(p.Snap.Right.Hit ? p.Snap.Right.Rest - p.Snap.Right.AnkleAboveGround : 0f):F3}"),
            this.Columns("planted", () => $"{p.Snap.Left.Planted:F2}", () => $"{p.Snap.Right.Planted:F2}"),
            this.Columns("can extend", () => $"{p.Snap.Left.MaxExtend:F3}", () => $"{p.Snap.Right.MaxExtend:F3}"),
            this.Columns("can raise", () => $"{p.Snap.Left.MaxRaiseByKnee:F3}", () => $"{p.Snap.Right.MaxRaiseByKnee:F3}"),
            this.Columns("offset", () => $"{p.Snap.Left.Offset:F3}", () => $"{p.Snap.Right.Offset:F3}"),
            this.Columns("contact", () => $"{p.Snap.Left.Contact:F2}", () => $"{p.Snap.Right.Contact:F2}"),
            this.Columns("tilt", () => $"{p.Snap.Left.TiltDeg:F1}", () => $"{p.Snap.Right.TiltDeg:F1}"),
            this.Columns("yaw", () => $"{p.Snap.Left.YawDeg:F1}", () => $"{p.Snap.Right.YawDeg:F1}"),
            this.Columns("solved", () => YesNo(p.Snap.Left.Solved), () => YesNo(p.Snap.Right.Solved)),
            this.Columns("gather shift", () => Fmt(p.Snap.Left.GatherShift), () => Fmt(p.Snap.Right.GatherShift)),
            this.Columns("gather refused", () => p.Snap.Left.GatherBlock.ToString(), () => p.Snap.Right.GatherBlock.ToString()),
            this.Columns("wall push", () => Fmt(p.Snap.Left.WallShift), () => Fmt(p.Snap.Right.WallShift)),
            this.Columns("ankle (model)", () => Fmt(p.Snap.Left.AnkleModel), () => Fmt(p.Snap.Right.AnkleModel)),
            this.Columns("ground point", () => Fmt(p.Snap.Left.HitPoint), () => Fmt(p.Snap.Right.HitPoint)),
            this.Columns("ground normal", () => Fmt(p.Snap.Left.HitNormal), () => Fmt(p.Snap.Right.HitNormal)),
            this.Columns("material", () => $"0x{p.Snap.Left.Material:X}", () => $"0x{p.Snap.Right.Material:X}"),
        ];
    }

    private string Metres(float fraction, int digits = 2) => (fraction * this.Owner.Snap.LegLength).ToString($"F{digits}");

    private static bool Hooked(Plugin p) =>
        p.HookStatus.StartsWith("hooked", StringComparison.Ordinal) && p.MoveHookStatus.StartsWith("hooked", StringComparison.Ordinal) && p.SelfTest == "PASS";

    private static string Activity(in Snapshot s, Where where)
    {
        if (where == Where.Off)
        {
            return "Switched off.";
        }

        if (!s.HasPose)
        {
            return "No character to work on.";
        }

        if (s.GPose && where == Where.InGame)
        {
            return "Paused in group pose.";
        }

        if (!s.GPose && where == Where.GroupPose)
        {
            return "Waiting for group pose.";
        }

        if (s.Conditions)
        {
            return "Paused: mounted, swimming, flying, in a cutscene or loading.";
        }

        if (s.IsJumping)
        {
            return "Paused while jumping.";
        }

        if (s.OnFloor)
        {
            return "Lying on the floor: only the body tilt applies.";
        }

        if (!s.Gate)
        {
            return $"Paused during {s.Mode}.";
        }

        return s.Blend < 0.99f ? $"Fading in, {s.Blend:P0}." : "Placing feet.";
    }

    private static string Cm(float metres) => $"{metres * 100f:+0;-0;0} cm";

    private static string State(in FootSnapshot f) => f.Gathered ? "gathered" : f.OverEdge ? "over edge" : "on ground";

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Fmt(Vector3 v) => $"{v.X:F2}, {v.Y:F2}, {v.Z:F2}";

    // The world markers stay on ImGui: they are drawn over the scene rather than in a window, and the game's own
    // node tree has nothing that draws a line between two screen points.
    public void DrawWorldDots()
    {
        ref var s = ref this.Owner.Snap;
        var c = this.Owner.Settings;
        if (!s.HasPose || (!c.ShowMarkers && !c.ShowRuler))
        {
            return;
        }

        var dl = ImGui.GetBackgroundDrawList();
        if (c.ShowMarkers)
        {
            DrawFootDots(dl, in s.Left);
            DrawFootDots(dl, in s.Right);
        }

        if (c.ShowRuler)
        {
            DrawRuler(dl, in s);
        }
    }

    // One metre along the character's right on the ground under the body and one metre up from it, a tick every ten centimetres.
    private static void DrawRuler(ImDrawListPtr dl, in Snapshot s)
    {
        var side = new Vector3(MathF.Cos(s.Yaw), 0f, -MathF.Sin(s.Yaw));
        DrawRuler(dl, s.Ground, side, Vector3.UnitY);
        DrawRuler(dl, s.Ground, Vector3.UnitY, side);
    }

    private static void DrawRuler(ImDrawListPtr dl, Vector3 from, Vector3 along, Vector3 tick)
    {
        if (!Plugin.GameGui.WorldToScreen(from, out var a) || !Plugin.GameGui.WorldToScreen(from + along, out var b))
        {
            return;
        }

        dl.AddLine(a, b, 0xFFFFFFFF, 2f);
        for (var i = 0; i <= 10; i++)
        {
            var p = from + (along * (0.1f * i));
            var t = tick * (i % 5 == 0 ? 0.06f : 0.03f);
            if (Plugin.GameGui.WorldToScreen(p, out var p0) && Plugin.GameGui.WorldToScreen(p + t, out var p1))
            {
                dl.AddLine(p0, p1, 0xFFFFFFFF, 2f);
            }
        }

        dl.AddText(b + new Vector2(6f, -8f), 0xFFFFFFFF, "1 m");
    }

    private static void DrawFootDots(ImDrawListPtr dl, in FootSnapshot f)
    {
        if (Plugin.GameGui.WorldToScreen(f.AnkleWorld, out var a))
        {
            dl.AddCircleFilled(a, 5f, 0xFF00FF00);
        }

        if (f.Solved && Plugin.GameGui.WorldToScreen(f.IkTargetWorld, out var t))
        {
            dl.AddCircle(t, 6f, 0xFF00FFFF, 0, 2f);
        }

        if (f.Hit && Plugin.GameGui.WorldToScreen(f.HitPoint, out var g) && Plugin.GameGui.WorldToScreen(f.HitPoint + f.HitNormal * 0.25f, out var n))
        {
            dl.AddLine(g, n, 0xFFFFFF00, 2f);
        }
    }

    // Reset asks first, so a long afternoon of tuning is never one misclick from gone.
    private sealed class ConfirmWindow : NativeAddon
    {
        public string Prompt = string.Empty;
        public string Label = string.Empty;
        public Action? OnConfirm;

        protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
        {
            base.OnSetup(addon, atkValueSpan);

            var text = new TextNode
            {
                Width = this.ContentSize.X,
                Height = LineHeight,
                String = this.Prompt,
                AlignmentType = AlignmentType.TopLeft,
                LineSpacing = (uint)LineHeight,
                TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            };
            Fit(text);

            var yes = new TextButtonNode
            {
                Height = BarHeight,
                Width = 160f,
                String = this.Label,
                OnClick = () =>
                {
                    this.OnConfirm?.Invoke();
                    this.Close();
                },
            };

            var no = new TextButtonNode
            {
                Height = BarHeight,
                Width = 100f,
                String = "Cancel",
                OnClick = this.Close,
            };

            new VerticalListNode
            {
                Position = this.ContentStartPosition,
                Size = this.ContentSize,
                ItemSpacing = Gap * 2f,
                InitialNodes =
                [
                    text,
                    new HorizontalListNode
                    {
                        Height = BarHeight,
                        Width = this.ContentSize.X,
                        ItemSpacing = Gap * 2f,
                        InitialNodes = [yes, no],
                    },
                ],
            }.AttachNode(this);
        }
    }
}
