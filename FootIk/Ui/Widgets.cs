using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace FootIk;

internal sealed partial class Overlay
{
    // Fixed width and ellipsis: an auto-sized node rewrites its width on every SetText and walks over its row.
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

    // Built on first open. Toggling rebuilds the tab next frame: re-flowing around a resized header misplaced rows,
    // and the header's own click is still on the stack.
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

    private NodeBase Check(ReadOnlySeString label, Func<bool> get, Action<bool> set, string help, Func<bool>? enabled = null)
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

    // No FitWidth: the value text hangs off the slider's right end.
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

        // The component's value text past the track, carrying the setting instead of the step count.
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
    private NodeBase Columns(string name, Func<string> left, Func<string> right, bool dim = false)
    {
        var width = (this.rowWidth - LabelWidth - (Gap * 2f)) / 2f;
        var l = this.Label(string.Empty, width, dim);
        var r = this.Label(string.Empty, width, dim);
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

    private NodeBase ColumnHeads() => this.Columns(" ", () => "Left", () => "Right", dim: true);

    private string Metres(float fraction, int digits = 2) => (fraction * this.Owner.Snap.LegLength).ToString($"F{digits}");
}
