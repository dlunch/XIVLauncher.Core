using System.Numerics;

using Hexa.NET.ImGui;

using XIVLauncher.Core.Components.Common;

namespace XIVLauncher.Core.Components.MainPage;

public sealed class KoreanLoginFrame : Component, IDisposable
{
    private const string LoginActionPopupId = "koreanLoginAction";

    private readonly MainPage mainPage;
    private readonly Input loginInput;
    private readonly Input passwordInput;
    private readonly Input captchaInput;
    private TextureWrap? captchaTexture;

    public KoreanLoginFrame(MainPage mainPage)
    {
        this.mainPage = mainPage;

        this.loginInput = new Input("Account", "Korean FFXIV account", new Vector2(12f, 0f), 128);
        this.passwordInput = new Input("Password", "Password", new Vector2(12f, 0f), 128, flags: ImGuiInputTextFlags.Password | ImGuiInputTextFlags.NoUndoRedo);
        this.captchaInput = new Input("CAPTCHA", "Enter the characters shown above", new Vector2(12f, 0f), 32);

        this.loginInput.Enter += () => TryLogin(LoginAction.Game);
        this.passwordInput.Enter += () => TryLogin(LoginAction.Game);
        this.captchaInput.Enter += () => TryLogin(LoginAction.Game);
    }

    public event Action<LoginAction>? OnLogin;

    public event Action? OnRefreshCaptcha;

    public string Username
    {
        get => this.loginInput.Value;
        set => this.loginInput.Value = value;
    }

    public string Password
    {
        get => this.passwordInput.Value;
        set => this.passwordInput.Value = value;
    }

    public string Captcha => this.captchaInput.Value;

    public bool IsCaptchaLoading { get; set; }

    public string? CaptchaError { get; set; }

    public void SetCaptcha(byte[] imageBytes)
    {
        this.captchaTexture?.Dispose();
        this.captchaTexture = TextureWrap.Load(imageBytes);
        this.captchaInput.Value = string.Empty;
        this.CaptchaError = null;
    }

    public void ClearCaptcha()
    {
        this.captchaTexture?.Dispose();
        this.captchaTexture = null;
        this.captchaInput.Value = string.Empty;
    }

    public override void Draw()
    {
        var viewport = ImGuiHelpers.ViewportSize;
        if (ImGui.BeginChild("###koreanLoginFrame", new Vector2(-1, viewport.Y - 128f)))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(32f, 32f));

            this.loginInput.Draw();
            this.passwordInput.Draw();

            if (this.captchaTexture != null)
            {
                var availableWidth = ImGui.GetContentRegionAvail().X;
                var scale = Math.Min(1f, availableWidth / this.captchaTexture.Width);
                var imageSize = new Vector2(this.captchaTexture.Width * scale, this.captchaTexture.Height * scale);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (availableWidth - imageSize.X) / 2));
                ImGui.Image(this.captchaTexture.ImGuiHandle, imageSize);
            }
            else
            {
                ImGui.TextDisabled(this.IsCaptchaLoading ? "Loading CAPTCHA…" : "CAPTCHA is unavailable.");
            }

            if (!string.IsNullOrEmpty(this.CaptchaError))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.TextWrapped(this.CaptchaError);
                ImGui.PopStyleColor();
            }

            this.captchaInput.IsEnabled = this.captchaTexture != null && !this.IsCaptchaLoading;
            this.captchaInput.Draw();

            if (ImGui.Button(this.IsCaptchaLoading ? "Refreshing…" : "Refresh CAPTCHA"))
                this.OnRefreshCaptcha?.Invoke();

            var canSubmit = this.captchaTexture != null
                            && !this.IsCaptchaLoading
                            && !string.IsNullOrWhiteSpace(this.Username)
                            && !string.IsNullOrEmpty(this.Password)
                            && !string.IsNullOrWhiteSpace(this.Captcha);

            if (!canSubmit)
                ImGui.BeginDisabled();
            if (ImGui.Button("Log in", new Vector2(-1, 0)))
                TryLogin(LoginAction.Game);
            if (!canSubmit)
                ImGui.EndDisabled();

            ImGui.PopStyleVar();

            ImGui.OpenPopupOnItemClick(LoginActionPopupId, ImGuiPopupFlags.MouseButtonRight);
            if (ImGui.BeginPopupContextItem(LoginActionPopupId))
            {
                if (ImGui.MenuItem("Launch without Dalamud"))
                    TryLogin(LoginAction.GameNoDalamud);
                if (ImGui.MenuItem("Launch without plugins"))
                    TryLogin(LoginAction.GameNoPlugins);
                if (ImGui.MenuItem("Launch without third-party plugins"))
                    TryLogin(LoginAction.GameNoThirdparty);
                if (ImGui.MenuItem("Patch without launching"))
                    TryLogin(LoginAction.GameNoLaunch);
                ImGui.EndPopup();
            }

            if (ImGui.Button(FontAwesomeIcon.UserFriends.ToIconString(), new Vector2(45) * ImGuiHelpers.GlobalScale))
                this.mainPage.AccountSwitcher.Open();
        }

        ImGui.EndChild();
        base.Draw();
    }

    public void Dispose()
    {
        this.ClearCaptcha();
    }

    private void TryLogin(LoginAction action)
    {
        if (this.captchaTexture != null
            && !this.IsCaptchaLoading
            && !string.IsNullOrWhiteSpace(this.Username)
            && !string.IsNullOrEmpty(this.Password)
            && !string.IsNullOrWhiteSpace(this.Captcha))
        {
            this.OnLogin?.Invoke(action);
        }
    }
}
