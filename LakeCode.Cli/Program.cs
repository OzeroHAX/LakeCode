using LakeCode.Cli.Components;
using LakeCode.Cli;
using LakeCode.Cli.Agent;
using LakeCode.Cli.Agent.Tools;
using LakeCode.Cli.State;
using LakeCode.Cli.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Configuration;

var workspaceRoot = WorkspaceFiles.ResolveRoot();
var shutdown = new CancellationTokenSource();
var modelStore = new ModelStore(ModelCatalog.Models, "gpt-6.1-sol");
var statisticsStore = new StatisticsStore(ModelCatalog.Models["gpt-6.1-sol"]);
var modelsWindow = new ModelsWindowComponent(modelStore);
var sessionsWindow = new SessionsWindowComponent();
var sessionStorage = new SessionStorage();

var windowSystem = new ConsoleWindowSystem(
    new NetConsoleDriver(RenderMode.Buffer),
    options: new ConsoleWindowSystemOptions(ShowTopPanel: false, ShowBottomPanel: false));
windowSystem.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.C, () =>
{
    shutdown.Cancel();
    windowSystem.Shutdown();
});

var header = new HeaderComponent("LakeCode", "Coding agent");
var mainContent = new MainContentComponent();
using var input = new InputComponent(windowSystem, shutdown.Token);
var footer = new FooterComponent(windowSystem, modelStore, statisticsStore, workspaceRoot);
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
    .AddControl(footer)
    .Build();
input.Attach(window);
stopGeneration.Attach(window);
window.OnClosing += (_, _) => shutdown.Cancel();
window.PreviewKeyPressed += (_, e) =>
{
    if (!e.Handled && e.KeyInfo.Key == ConsoleKey.Tab && e.KeyInfo.Modifiers == ConsoleModifiers.Shift)
    {
        modelStore.CycleReasoningEffort();
        e.Handled = true;
    }
};
chat = new ChatController(
    windowSystem, mainContent, header, input, statisticsStore, modelStore,
    () => window.FocusControl(input.Editor), workspaceRoot, sessionStorage, sessionsWindow, shutdown.Token);

windowSystem.AddWindow(window);
using var slashCommands = new SlashCommandsComponent(window, input,
[
    ("/exit", "Завершить LakeCode"),
    ("/new", "Начать новую сессию"),
    ("/models", "Показать доступные модели"),
    ("/sessions", "Открыть сохранённую сессию")
]);
slashCommands.CommandSelected += (_, command) =>
{
    if (command.Equals("/new", StringComparison.OrdinalIgnoreCase))
    {
        chat.StartNewSession();
    }
    else if (command.Equals("/sessions", StringComparison.OrdinalIgnoreCase))
    {
        chat.ShowSessions();
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
}
