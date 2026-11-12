# LakeCode.Agent.Kernel

Нативное F#-ядро без MAF, UI и провайдерских SDK. Пока не подключено к Core/CLI. Один владелец состояния, последовательный цикл «модель → инструменты → модель» и live-события без журнала.

## Публичные сущности

- `Session` принимает запросы, владеет сообщениями и рассылает события.
- `Message` — `System`, `User`, `Assistant`, `Tool`.
- `Content` — общий текст и список оригинальных `Image` для пользовательских сообщений, ответов модели и результатов инструментов.
- `Model` — функция, возвращающая `IAsyncEnumerable<ModelUpdate>`. Отдельного режима полного ответа нет.
- `Tool` — имя, описание, JSON Schema и функция исполнения в одном record. `ToolDescription` — только модельная часть. `ToolCall` — данные вызова, `ToolResult` — результат.
- `ToolPolicy` — только допуск вызова: `Allow`, `Deny reason`, `Stop reason`. Подготовка и завершающие hooks принадлежат host.
- `RequestId` связывает события и отмену с конкретным запросом. `RequestHandle.Completion` сообщает причину завершения без повторного текста ответа.
- `RewindPoint` — проверяемая граница отката, привязанная к сообщениям ветки.

Публичных `SessionStatus`, `RunPhase`, `RunView`, `SessionSnapshot`, `Revision`, `LastRun`, `RunResult` и объекта `History` нет. История — сообщения сессии; её проверенное внутреннее представление не является публичным API.

## Операции сессии

- `Start(Message.User(id, content), ?cancellationToken)` возвращает `Result<RequestHandle, SessionError>`. Другие роли на входе Start не допускаются. Текстовый overload `Start(string)` создаёт пользовательское сообщение с новым id.
- `Messages` возвращает неизменяемый список сообщений текущей ветки. Его можно передать в `messages` при создании другой сессии. Восстановление проверяет историю и запрещает незакрытую группу вызовов.
- `RewindPoints` возвращает допустимые точки: перед user, после assistant без вызовов, после всей группы результатов инструментов.
- `Rewind(point)` меняет ветку только вне обработки запроса. Устаревшая точка не может попасть на ту же позицию другой ветки. Побочные эффекты не откатываются.
- `Cancel(requestId)` запрашивает отмену именно этого запроса; для другого или завершённого возвращает `false`.
- `Close()` запрещает новые запросы, отменяет текущий и ждёт cleanup. Возвращает общую задачу закрытия. Поддерживается `IAsyncDisposable`.

`SessionError` различает `Busy`, `Closed`, `InvalidHistory`. Модель, каталог инструментов, policy и лимит обращений захватываются при создании сессии. Адаптеры не должны читать вместо них изменяемую глобальную конфигурацию.

## Сгруппированные события

`EventOccurred` имеет делегат `Action<KernelEvent>`. События только live, без `ReadEvents`, `WaitForEvents` и replay.

```text
KernelEvent
├── Request(requestId, event)
│   ├── Started
│   └── Finished(reason)
├── Output(requestId, blockId, event)
│   ├── Started(Text | Thinking)
│   ├── Delta(text)
│   └── Finished
├── Tool(requestId, callId, event)
│   ├── Proposed(name)
│   ├── ArgumentsDelta(text)
│   ├── Ready(call)
│   ├── Started
│   └── Progress(Text | Data)
└── History(event)
    ├── Added(message)
    └── Rewound(point)
```

`Tool.Proposed` — модель формирует вызов. `Tool.Started` — ядро начинает исполнение. Это разные события. Результат инструмента приходит только через `History.Added(Message.Tool ...)`, полный ответ модели — через `History.Added(Message.Assistant ...)`. Нет дублирующих `ModelFinished`, `ToolFinished`, событий model-turn или промежуточной финализации.

`FinishReason` различает `Completed`, `Canceled`, `Stopped reason`, `Failed(stage, message)`. Это причина окончания, не состояние исполнения. `Request.Finished` публикуется после cleanup, до завершения `Completion`.

Пример обработки без UI:

```fsharp
let onEvent = System.Action<KernelEvent>(fun event ->
    match event with
    | KernelEvent.Output(_, blockId, OutputEvent.Started OutputKind.Thinking) ->
        printfn "Thinking block: %s" blockId
    | KernelEvent.Output(_, blockId, OutputEvent.Started OutputKind.Text) ->
        printfn "Text block: %s" blockId
    | KernelEvent.Output(_, _, OutputEvent.Delta text) -> printf "%s" text
    | KernelEvent.History(HistoryEvent.Added message) -> printfn "%A" message
    | _ -> ())

session.EventOccurred.AddHandler(onEvent)
// Когда наблюдение закончено:
session.EventOccurred.RemoveHandler(onEvent)
```

Callback выполняется на потоке, которая опубликовала событие; отдельного диспетчера событий нет. Исключения подписчиков не влияют на запрос и не сохраняются. Обработчики должны только ставить работу в очередь host: медленный callback задерживает текущую операцию. Из callback нельзя синхронно вызывать Start, Cancel или Rewind. Читать Messages можно; Close можно запросить, но не ждать его в callback.

## Блоки модели

Адаптер явно выдаёт `ModelUpdate.Output(blockId, Started kind)`, дельты и `Finished`. Id непустой и уникальный в пределах одного ответа модели; ядро добавляет префикс номера обращения, поэтому публичный blockId уникален в запросе. Соседние thinking-блоки можно различить даже без текстового блока между ними. Между блоками не вставляются придуманные разделители.

Дельта без открытого блока, повторный id и повторное закрытие — ошибки протокола. Обычный конец потока с открытым блоком также ошибка. При отмене или ошибке ядро публикует Finished для оставшихся открытых блоков; причину прерывания сообщает Request.Finished. Накопленный текст сохраняется в Assistant со статусом Interrupted. Thinking передаётся только событиями и не входит в модельную историю.

Инструменты поступают как `ToolProposed`, `Arguments`, `ToolReady`. Для полного готового вызова достаточно ToolReady без proposal. Проверяются имя, уникальность call id в сохранённой ветке и завершённость вызовов. Вызовы исполняются по порядку первого появления. После группы результатов модель вызывается снова.

## Владение данными, отмена и ошибки

JSON schemas, аргументы и структурный progress захватываются клонированием. Producer обязан сохранять JsonDocument живым до передачи значения ядру. Модель получает только `ToolDescription`, без исполняющих функций. Каждое обновление обрабатывается и публикуется непосредственно до следующего MoveNextAsync, без очереди и reply channels; при ошибке протокола iterator освобождается до завершения запроса.

Изображения содержат оригинальные ImmutableArray<byte>. История проверяет наличие байтов, media type и положительные размеры. Декодирование, формат, лимиты и поддержку моделью проверяет адаптер. Backing storage после передачи изменять нельзя.

Отмена кооперативная: неотменяемая операция не отсоединяется, Close ждёт её. Результат, полученный после отмены, сохраняется; следующий инструмент или модель не запускается. Поздний progress после возврата инструмента не принимается. Автоматических повторов нет.

ToolResult различает Success, Failure, Skipped и Interrupted. Последнее означает, что вызов начался, результата нет и побочные эффекты могли произойти. Незавершённый JSON не превращается в готовый вызов; полученные, но не исполненные вызовы закрываются Skipped. Адаптер должен явно передавать эти исходы и ReplyStatus.Interrupted в протокол модели либо отклонить неподдерживаемое представление.

`Failed(stage, message)` содержит сообщение ошибки, которое тоже может включать приватные данные; host должен явно выбирать, что отображать. Ошибки cleanup не меняют основной исход. Отдельного API Faults нет: неожиданное исключение цикла запроса становится `Failed("runtime", message)`.

Необязательный параметр `Session.maxModelCalls` ограничивает число обращений к модели и требует положительного значения. Если он не передан, ограничения нет. Policy по умолчанию разрешает вызовы. Это не sandbox: технические гарантии файлов, процессов и permissions обеспечивают инструменты и host.

## Внутреннее исполнение

`Session.fs` хранит историю, принимает запросы, публикует события и завершает запрос. `Execution.fs` содержит последовательный async-цикл: чтение модели, сохранение ответа, policy, инструменты, следующее обращение к модели. `ModelResponse` — внутренний накопитель одного ответа.

Нет session-state машины, Input/Effect, mailbox, per-operation workers и обратных сообщений. Текущий запрос и закрытие защищены блокировкой; внешние операции ожидаются вне неё. Отмена записывается один раз и запускает callbacks; завершение ждёт их и освобождает cancellation source. После возврата инструмента поздний progress не публикуется. Файловая persistence и сохранение прошлых thinking/progress принадлежат host; перечитать из Messages можно только сообщения.

## Проверка

Из корня репозитория:

```text
dotnet build LakeCode.Agent.Kernel/LakeCode.Agent.Kernel.fsproj
dotnet build LakeCode.Cli/LakeCode.Cli.csproj
```

CLI пока не ссылается на ядро; его сборка не заменяет отдельную сборку ядра. Тесты и приложение запускаются только по отдельному запросу пользователя.
