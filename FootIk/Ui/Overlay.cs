using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace FootIk;

// The settings window is built from native game nodes. They are destroyed when the window closes, so every node
// reference is rebuilt in OnSetup and dropped in OnFinalize; nothing here survives a close.
internal sealed partial class Overlay : NativeAddon
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

    // Name, builder, and the fields its reset puts back; null resets everything. Each tab lives in Ui/Tabs.
    private (string Name, Func<List<NodeBase>> Build, string[]? Fields)[] Tabs => this.tabs ??=
    [
        ("Feet", this.BuildFeet, FeetFields),
        ("Edges", this.BuildEdges, EdgesFields),
        ("Leaning", this.BuildLean, LeanFields),
        ("Shoving", this.BuildShoving, ShovingFields),
        ("Emotes", this.BuildEmotes, EmotesFields),
        ("Performance", this.BuildPerformance, PerformanceFields),
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
            Size = new Vector2(820f, 620f), // wide enough for seven tabs, "Performance" the longest
        });
    }

    // The other half of the same arrangement: Dalamud calls DisposeAsync off the framework thread, so the hooks
    // and our own offsets have to be put back from a call marshalled onto it.
    internal static async ValueTask ShutdownAsync(Plugin owner, Overlay? window)
    {
        try
        {
            // Run, not RunOnFrameworkThread: awaiting the latter resumes this method on the framework thread, where
            // NativeAddon.CloseAsync never finishes because it waits out a closing animation that needs frames to pass.
            await Plugin.Framework.Run(owner.Teardown);
        }
        catch (Exception ex)
        {
            // Whatever the hooks and the offsets did, the window still has to come down: native nodes left attached
            // outlive the plugin that owns them.
            Plugin.Log.Error(ex, "FootIk: teardown failed");
        }

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

            // Visibility belongs to the fold; an empty line holds a blank row instead.
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

    // Text nodes do not clip: grow to the wrapped height or the overflow draws over the next row.
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

        // Content height first, or the bar sizes against the old list. Never move the content by hand: the bar owns it.
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
