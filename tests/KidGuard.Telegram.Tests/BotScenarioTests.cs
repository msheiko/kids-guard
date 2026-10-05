using System.Text.RegularExpressions;
using KidGuard.Core;

namespace KidGuard.Telegram.Tests;

/// <summary>Сценарии приёмки 14–18 и основные действия бота.</summary>
public class BotScenarioTests
{
    const long Mom = BotFixture.MomId;
    const long Dad = BotFixture.DadId;
    const long Stranger = BotFixture.StrangerId;

    [Fact]
    public async Task Scenario14_CommandsWhilePcWasOff_StateAppliedOneOffRejected()
    {
        var f = new BotFixture();
        f.Bot.ProcessBatch(
            [
                f.Text(Mom, "/off", TimeSpan.FromMinutes(20)),
                f.Text(Mom, "/add 30", TimeSpan.FromMinutes(19)),
            ],
            callbackAge: TimeSpan.FromMinutes(20));
        await f.Drain();

        var status = f.H.Engine.GetStatus();
        Assert.False(status.Access);
        Assert.False(f.H.Account.Enabled);
        Assert.Equal(TimeSpan.Zero, status.TodayBonus);

        Assert.Contains(f.Api.To(Mom), m => m.Text == BotTexts.Stale("/add 30"));
        foreach (var parent in new[] { Mom, Dad })
        {
            var summary = Assert.Single(f.Api.To(parent), m => m.Text.StartsWith("📬", StringComparison.Ordinal));
            Assert.Contains("/off (Мама) — выполнено", summary.Text);
            Assert.Contains("/add 30 (Мама) — устарела", summary.Text);
        }
    }

    [Fact]
    public async Task DelayedStateCommands_OnlyLastOfEachTypeApplied()
    {
        var f = new BotFixture();
        f.Bot.ProcessBatch(
        [
            f.Text(Mom, "/off", TimeSpan.FromHours(3)),
            f.Text(Dad, "/on", TimeSpan.FromHours(2)),
            f.Text(Mom, "/limit 45", TimeSpan.FromHours(1)),
        ]);
        await f.Drain();

        var status = f.H.Engine.GetStatus();
        Assert.True(status.Access);
        Assert.Equal(45, status.SessionLimitMinutes);
        Assert.Contains(f.Api.To(Mom), m => m.Text.Contains("/off (Мама) — заменена"));
    }

    [Fact]
    public async Task FreshCommand_NoSummary()
    {
        var f = new BotFixture();
        await f.Process(f.Text(Mom, "/add 15"));

        Assert.DoesNotContain(f.Api.Sent, m => m.Text.StartsWith("📬", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromMinutes(15), f.H.Engine.GetStatus().TodayBonus);
    }

    [Fact]
    public async Task Scenario15_TimeRequest_FirstParentAnswers_OtherMessageEdited()
    {
        var f = new BotFixture();
        f.H.Engine.SetSessionLimit(60, Harness.Mom);
        f.H.Login();
        Assert.Equal(TimeRequestOutcome.Sent, f.H.Engine.RequestTime("доделать проект").Outcome);
        await f.Drain();

        var requests = f.Api.Sent.Where(m => m.Keyboard?.Buttons.Any(b => b.Data.StartsWith("tr:", StringComparison.Ordinal)) == true).ToList();
        Assert.Equal(new[] { Mom, Dad }, requests.Select(r => r.ChatId).OrderBy(c => c));
        Assert.All(requests, r => Assert.Contains("доделать проект", r.Text));

        var momMessage = requests.Single(r => r.ChatId == Mom);
        var plus30 = momMessage.Keyboard!.Buttons.Single(b => b.Text == "+30").Data;
        await f.Process(f.Button(Mom, plus30, momMessage.MessageId));

        var edits = f.Api.Edits.Where(e => e.Text.Contains("Обработано: +30 мин (Мама)")).ToList();
        Assert.Equal(new[] { Mom, Dad }, edits.Select(e => e.ChatId).OrderBy(c => c));
        Assert.All(edits, e => Assert.Null(e.Keyboard));
        Assert.Contains(f.Api.Answers, a => a.Text == "Обработано: +30 мин (Мама).");
        Assert.Contains(f.H.Tray.OfType(TrayMessage.TimeRequestResultType), m => m.Message.Text.Contains("+30"));
        Assert.Equal(TimeSpan.FromMinutes(90), f.H.Engine.GetStatus().SessionRemaining);

        // Второй родитель нажимает позже — время не добавляется повторно.
        var dadMessage = requests.Single(r => r.ChatId == Dad);
        await f.Process(f.Button(Dad, dadMessage.Keyboard!.Buttons.Single(b => b.Text == "+60").Data, dadMessage.MessageId));
        Assert.Equal(TimeSpan.FromMinutes(90), f.H.Engine.GetStatus().SessionRemaining);
        Assert.Equal(2, f.Api.Edits.Count(e => e.Text.Contains("Обработано")));
    }

    [Fact]
    public async Task Scenario17_StrangerIgnored_ParentsNotifiedAtMostHourly()
    {
        var f = new BotFixture();
        await f.Process(f.Text(Stranger, "привет", name: "Вася"));
        await f.Process(f.Text(Stranger, "/off", name: "Вася"));
        await f.Process(f.Button(Stranger, "a:on"));

        Assert.Empty(f.Api.To(Stranger));
        Assert.Empty(f.Api.Answers);
        Assert.True(f.H.Engine.GetStatus().Access);
        Assert.Equal(2, f.Api.Sent.Count(m => m.Text.Contains("посторонний") && m.Text.Contains("ID 99")));

        f.H.Clock.Advance(TimeSpan.FromMinutes(61));
        await f.Process(f.Text(Stranger, "ещё раз", name: "Вася"));
        Assert.Equal(4, f.Api.Sent.Count(m => m.Text.Contains("посторонний")));
    }

    [Fact]
    public async Task Scenario18_PairingCode_WrongOrExpiredRejected_ValidAccepted()
    {
        var f = new BotFixture();
        await f.Process(f.Text(Stranger, "/start 999999"));
        Assert.Contains(f.Api.To(Stranger), m => m.Text == BotTexts.PairingFailed);
        Assert.False(f.Parents.IsAllowed(Stranger));

        var expired = f.Pairing.CreateCode(f.H.Engine.TrustedUtcNow, TimeSpan.FromMinutes(10));
        f.H.Clock.Advance(TimeSpan.FromMinutes(11));
        await f.Process(f.Text(Stranger, "/start " + expired));
        Assert.Equal(2, f.Api.To(Stranger).Count(m => m.Text == BotTexts.PairingFailed));

        var valid = f.Pairing.CreateCode(f.H.Engine.TrustedUtcNow, TimeSpan.FromMinutes(10));
        await f.Process(f.Text(Stranger, "/start " + valid, name: "Бабушка"));

        Assert.True(f.Parents.IsAllowed(Stranger));
        Assert.Contains(Stranger, f.H.Config.Telegram.AllowedUserIds);
        Assert.Equal(1, f.ConfigSaves);
        Assert.Contains(f.Api.To(Stranger), m => m.Text == BotTexts.Paired);
        Assert.Contains(f.Api.To(Stranger), m => m.Keyboard?.Buttons.Any(b => b.Data == "a:off") == true);
        Assert.Contains(f.Api.To(Mom), m => m.Text.Contains("Добавлен родитель: Бабушка"));

        // Код одноразовый.
        await f.Process(f.Text(Stranger + 1, "/start " + valid));
        Assert.False(f.Parents.IsAllowed(Stranger + 1));
    }

    [Fact]
    public async Task Invite_CreatesCodeForSecondParent()
    {
        var f = new BotFixture();
        await f.Process(f.Text(Mom, "/invite"));
        var invite = Assert.Single(f.Api.To(Mom), m => m.Text.Contains("Код привязки"));
        var code = Regex.Match(invite.Text, @"\b\d{6}\b").Value;

        await f.Process(f.Text(Stranger, "/start " + code, name: "Бабушка"));
        Assert.True(f.Parents.IsAllowed(Stranger));
    }

    [Fact]
    public async Task GroupChat_BotLeaves()
    {
        var f = new BotFixture();
        await f.Process(f.Text(-100500, "/off", isPrivate: false), new BotUpdate(1000, GroupChatJoined: -200));

        Assert.Equal(new long[] { -100500, -200 }, f.Api.Left);
        Assert.True(f.H.Engine.GetStatus().Access);
    }

    [Fact]
    public async Task Status_SendsPanelWithButtons_AndRemembersIt()
    {
        var f = new BotFixture();
        await f.Process(f.Text(Mom, "/status"));

        var panel = Assert.Single(f.Api.To(Mom));
        Assert.Contains("🖥 ПК: kid-pc", panel.Text);
        Assert.Contains("🔓 Доступ: ВКЛЮЧЁН", panel.Text);
        Assert.Contains(panel.Keyboard!.Buttons, b => b.Data == "a:off");
        Assert.Equal(panel.MessageId, f.H.Engine.ReadState(s => s.Telegram.Panels[Mom]));
    }

    [Fact]
    public async Task PanelRefresh_EditsOnlyWhenTextChanges()
    {
        var f = new BotFixture();
        await f.Process(f.Text(Mom, "/status"));
        f.Bot.RefreshPanels();
        await f.Drain();
        Assert.Empty(f.Api.Edits);

        f.H.Login();
        f.H.RunMinutes(2);
        f.Bot.RefreshPanels();
        await f.Drain();
        var edit = Assert.Single(f.Api.Edits);
        Assert.Contains("👤 Сеанс: идёт 2 мин", edit.Text);
    }

    [Fact]
    public async Task OffButton_WithChildAtPc_AsksConfirmation()
    {
        var f = new BotFixture();
        f.H.Login();
        await f.Process(f.Button(Mom, "a:off"));

        var confirm = Assert.Single(f.Api.Edits);
        Assert.Contains("Ребёнок сейчас за ПК", confirm.Text);
        Assert.True(f.H.Engine.GetStatus().Access);

        await f.Process(f.Button(Mom, "a:offn"));
        Assert.False(f.H.Engine.GetStatus().Access);
        Assert.Single(f.H.Sessions.Logoffs);
        Assert.Contains(f.Api.Edits, e => e.Text.Contains("🔒 Доступ: ВЫКЛЮЧЕН (Мама)"));
    }

    [Fact]
    public async Task OffButton_WithoutSession_TurnsOffImmediately()
    {
        var f = new BotFixture();
        await f.Process(f.Button(Mom, "a:off"));

        Assert.False(f.H.Engine.GetStatus().Access);
        Assert.Contains(f.Api.Answers, a => a.Text == Texts.AccessOff);
    }

    [Fact]
    public async Task MessageButton_PromptsAndDeliversToChild()
    {
        var f = new BotFixture();
        f.H.Login();
        await f.Process(f.Button(Mom, "in:msg"));
        Assert.Contains(f.Api.To(Mom), m => m.ForceReply is not null);

        await f.Process(f.Text(Mom, "Пора ужинать!"));
        var message = Assert.Single(f.H.Tray.OfType(TrayMessage.MessageType)).Message;
        Assert.Equal("Пора ужинать!", message.Text);
        Assert.Equal("Мама", message.From);
    }

    [Fact]
    public async Task ScheduleCommand_SetsAndShowsSchedule()
    {
        var f = new BotFixture();
        await f.Process(f.Text(Mom, "/schedule будни 16:00-20:00"), f.Text(Mom, "/schedule on"));

        Assert.False(f.H.Account.Enabled);
        Assert.Equal(BlockReason.Schedule, f.H.Engine.GetStatus().Block);

        await f.Process(f.Text(Mom, "/schedule"));
        Assert.Contains(f.Api.To(Mom), m => m.Text.Contains("Пн: 16:00–20:00") && m.Text.Contains("Сб: запрещено"));
    }

    [Fact]
    public async Task InvalidCommand_RepliesWithUsage()
    {
        var f = new BotFixture();
        await f.Process(f.Text(Mom, "/add много"), f.Text(Mom, "/unknown"), f.Text(Mom, "просто текст"));

        var replies = f.Api.To(Mom).Select(m => m.Text).ToList();
        Assert.Equal(new[] { Texts.AddTimeInvalid, CommandParser.UnknownCommand, BotTexts.NotUnderstood }, replies);
    }

    [Fact]
    public async Task Notifications_GoToAllParents()
    {
        var f = new BotFixture();
        f.H.Login();
        await f.Drain();

        foreach (var parent in new[] { Mom, Dad })
        {
            Assert.Contains(f.Api.To(parent), m => m.Text.Contains("Ребёнок вошёл"));
        }
    }

    [Fact]
    public async Task RemoveParent_CannotRemoveLast()
    {
        var f = new BotFixture();
        await f.Process(f.Button(Mom, $"par:del:{Dad}"));
        Assert.False(f.Parents.IsAllowed(Dad));

        await f.Process(f.Button(Mom, $"par:del:{Mom}"));
        Assert.True(f.Parents.IsAllowed(Mom));
        Assert.Contains(f.Api.Answers, a => a.Text == BotTexts.LastParent);
    }

    [Fact]
    public async Task Today_ReportsSessionsAndActions()
    {
        var f = new BotFixture();
        f.H.Engine.SetSessionLimit(30, Harness.Mom);
        f.H.Login();
        f.H.RunMinutes(31);
        await f.Process(f.Text(Mom, "/today"));

        var report = f.Api.To(Mom).Last().Text;
        Assert.Contains("📊 Сегодня", report);
        Assert.Contains("(30 мин) — лимит сеанса", report);
        Assert.Contains("Мама: ⏱ Лимит сеанса: 30 мин.", report);
    }
}
