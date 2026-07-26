using System.Numerics;

using Hexa.NET.ImGui;

using XIVLauncher.Core.Configuration;

namespace XIVLauncher.Core.Components;

public class RegionSelectionPage : Page
{
    public RegionSelectionPage(LauncherApp app)
        : base(app)
    {
    }

    public override void Draw()
    {
        var viewport = ImGuiHelpers.ViewportSize;
        var contentWidth = Math.Min(560f, viewport.X - 64f);
        var buttonSize = new Vector2(contentWidth, 72f);

        ImGui.SetCursorPos(new Vector2((viewport.X - contentWidth) / 2f, Math.Max(48f, (viewport.Y - 260f) / 2f)));
        ImGui.BeginGroup();
        ImGui.Text("Select your game region");
        ImGui.TextDisabled("You can change this later in Settings.");
        ImGui.Dummy(new Vector2(0f, 24f));

        if (ImGui.Button("Global\nSquare Enix global service", buttonSize))
            App.SelectRegion(GameRegion.Global);

        ImGui.Dummy(new Vector2(0f, 12f));

        if (ImGui.Button("Korea\nActoz Korean service", buttonSize))
            App.SelectRegion(GameRegion.Korea);

        ImGui.EndGroup();

        base.Draw();
    }
}
