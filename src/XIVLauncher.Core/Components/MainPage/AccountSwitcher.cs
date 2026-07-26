using System.Numerics;

using Hexa.NET.ImGui;

using XIVLauncher.Core.Accounts;
using XIVLauncher.Core.Configuration;
using XIVLauncher.Core.Resources.Localization;

namespace XIVLauncher.Core.Components.MainPage;

public class AccountSwitcher : Component
{
    private const string ACCOUNT_SWITCHER_POPUP_ID = "accountSwitcher";

    private readonly AccountManager manager;

    private bool doOpen = false;

    public event EventHandler<XivAccount>? AccountChanged;

    public AccountSwitcher(AccountManager manager)
    {
        this.manager = manager;
    }

    public void Open()
    {
        this.doOpen = true;
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5));

        if (ImGui.BeginPopupContextItem(ACCOUNT_SWITCHER_POPUP_ID))
        {
            var region = Program.Config.GameRegion.GetValueOrDefault(GameRegion.Global);
            var accounts = this.manager.Accounts.Where(account => account.GameRegion == region).ToArray();

            if (accounts.Length == 0)
            {
                ImGui.Text(Strings.NoSavedAccounts);
            }

            foreach (XivAccount account in accounts)
            {
                var name = account.UserName;

                if (account.UseSteamServiceAccount)
                    name += " (Steam)";

                if (account.UseOtp)
                    name += " (OTP)";

                if (account.IsFreeTrial)
                    name += " (Trial)";

                var textLength = ImGui.CalcTextSize(name).X;

                if (ImGui.Button(name + $"###{account.Id}", new Vector2(textLength + 15, 40)))
                {
                    this.AccountChanged?.Invoke(this, account);
                }
            }

            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();

        if (this.doOpen)
        {
            this.doOpen = false;
            ImGui.OpenPopup(ACCOUNT_SWITCHER_POPUP_ID);
        }

        base.Draw();
    }
}
