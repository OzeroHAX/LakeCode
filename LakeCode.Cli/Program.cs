using LakeCode.Cli.Components;
using LakeCode.Cli;
using LakeCode.Cli.Agent;
using LakeCode.Cli.State;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Configuration;

var shutdown = new CancellationTokenSource();
var modelStore = new ModelStore(ModelCatalog.Models, "gpt-6.1-sol");
var statisticsStore = new StatisticsStore(ModelCatalog.Models["gpt-6.1-sol"]);
var modelsWindow = new ModelsWindowComponent(modelStore);

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
var input = new InputComponent();
var footer = new FooterComponent(windowSystem, modelStore, statisticsStore, Environment.CurrentDirectory);

var window = new WindowBuilder(windowSystem)
    .WithTitle("LakeCode")
    .Frameless()
    .Maximized()
    .AddControl(header)
    .AddControl(mainContent)
    .AddControl(input)
    .AddControl(footer)
    .Build();
window.OnClosing += (_, _) => shutdown.Cancel();
window.PreviewKeyPressed += (_, e) =>
{
    if (!e.Handled && e.KeyInfo.Key == ConsoleKey.Tab && e.KeyInfo.Modifiers == ConsoleModifiers.Shift)
    {
        modelStore.CycleReasoningEffort();
        e.Handled = true;
    }
};
var chat = new ChatController(
    windowSystem, mainContent, input, statisticsStore, modelStore,
    () => window.FocusControl(input.Editor), shutdown.Token);

windowSystem.AddWindow(window);
using var slashCommands = new SlashCommandsComponent(window, input,
[
    ("/exit", "Завершить LakeCode"),
    ("/models", "Показать доступные модели")
]);
slashCommands.CommandSelected += (_, command) =>
{
    if (command.Equals("/models", StringComparison.OrdinalIgnoreCase))
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
}
