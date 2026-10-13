using LakeCode.Cli.Components;
using LakeCode.Cli;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using LakeCode.Core.Conversations;
using LakeCode.Cli.State;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Configuration;

var workspaceRoot = ConversationService.ResolveWorkspaceRoot();
var shutdown = new CancellationTokenSource();
ProviderRegistry providers;
try { providers = new ProviderRegistry(); }
catch (Exception error)
{
    Console.Error.WriteLine($"Не удалось загрузить настройки провайдеров: {error.Message}");
    return;
}
var modelStore = new ModelStore(providers);
var statisticsStore = new StatisticsStore(modelStore.SelectedModel);
var modelsWindow = new ModelsWindowComponent(modelStore);
var providersWindow = new ProvidersWindowComponent(providers, modelStore, shutdown.Token);
var sessionsWindow = new SessionsWindowComponent();
var conversation = new ConversationService(workspaceRoot, providers, modelStore.SelectedModel, shutdown.Token);

var windowSystem = new ConsoleWindowSystem(
    new NetConsoleDriver(RenderMode.Buffer),
    options: new ConsoleWindowSystemOptions(ShowTopPanel: false, ShowBottomPanel: false));

var header = new HeaderComponent("LakeCode", "Coding agent");
var mainContent = new MainContentComponent();
using var input = new InputComponent(windowSystem, shutdown.Token);
var footer = new FooterComponent(windowSystem, modelStore, statisticsStore, workspaceRoot);
using var btw = new BtwWindowComponent(windowSystem);
var askHost = new SharpConsoleUI.Controls.PanelControl
{
    Visible = false,
    HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment.Stretch
};
ChatController? chat = null;
using var stopGeneration = new StopGenerationComponent(windowSystem, statisticsStore,
    () => chat?.CanStopGeneration == true, () => chat?.StopGeneration());

var window = new WindowBuilder(windowSystem)
    .WithTitle("LakeCode")
    .Frameless()
    .Maximized()
    .AddControl(header)
    .AddControl(mainContent)
    .AddControl(stopGeneration)
    .AddControl(input)
    .AddControl(askHost)
    .AddControl(footer)
    .Build();
input.Attach(window);
stopGeneration.Attach(window);
window.OnClosing += (_, _) => shutdown.Cancel();
window.PreviewKeyPressed += (_, e) =>
{
    if (!btw.IsOpen && !e.Handled && e.KeyInfo.Key == ConsoleKey.Tab && e.KeyInfo.Modifiers == ConsoleModifiers.Shift)
    {
        modelStore.CycleReasoningEffort();
        e.Handled = true;
    }
};
chat = new ChatController(
    windowSystem, mainContent, header, input, statisticsStore, modelStore,
    () => window.FocusControl(input.Editor), sessionsWindow, askHost, conversation, workspaceRoot, btw);

windowSystem.AddWindow(window);
using var rewindWindow = new RewindWindowComponent(windowSystem, chat);
using var rewind = new RewindComponent(window, input, chat, rewindWindow);
using var fileMentions = new FileMentionsComponent(window, windowSystem, input, workspaceRoot, shutdown.Token);
using var slashCommands = new SlashCommandsComponent(window, input,
[
    ("/exit", "Завершить LakeCode"),
    ("/new", "Начать новую сессию"),
    ("/models", "Показать доступные модели"),
    ("/providers", "Настроить подключения и модели"),
    ("/sessions", "Открыть сохранённую сессию"),
    ("/btw", "Попутный вопрос без изменения основной истории"),
    ("/debug", "Показать или скрыть usage (MAF)")
]);
slashCommands.CommandSelected += (_, selected) =>
{
    var command = selected.Name;
    if (command.Equals("/btw", StringComparison.OrdinalIgnoreCase))
    {
        chat.ShowOrAskSideQuestion(selected.Arguments);
        return;
    }
    if (command.Equals("/new", StringComparison.OrdinalIgnoreCase))
    {
        chat.StartNewSession();
    }
    else if (command.Equals("/sessions", StringComparison.OrdinalIgnoreCase))
    {
        chat.ShowSessions();
    }
    else if (command.Equals("/debug", StringComparison.OrdinalIgnoreCase))
    {
        input.Editor.Input = string.Empty;
        footer.ToggleDebug();
    }
    else if (command.Equals("/providers", StringComparison.OrdinalIgnoreCase))
    {
        input.Editor.Input = string.Empty;
        providersWindow.Show(windowSystem);
    }
    else if (command.Equals("/models", StringComparison.OrdinalIgnoreCase))
    {
        input.Editor.Input = string.Empty;
        modelsWindow.Show(windowSystem);
    }
};
window.FocusControl(input.Editor);
try
{
    windowSystem.Run();
}
finally
{
    footer.StopAnimation();
    shutdown.Cancel();
    chat.FinishAsync().GetAwaiter().GetResult();
    chat.Dispose();
}
