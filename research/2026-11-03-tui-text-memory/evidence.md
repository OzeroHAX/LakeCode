# Доказательства

Дата доступа ко всем внешним источникам: 2026-11-03.
Исследование архитектурных приёмов, не аудит пакетов для установки.

Локальные основания: профили /tmp/lakecode-2193624-allocations-{a,b}.txt;
второй профиль — 707002128 sampled bytes для Cell[] за 20.451 с.
Основной стек: ParseCore / ParseExpandedLines / ParseSource / MeasureDOM,
а также WrapCellLine / WrapParsedLines. Сессии разных запусков несопоставимы
как контролируемый A/B-тест.

Исходный код: Cell — struct, поэтому проблема не в отдельном heap-объекте
на каждый Cell, а в массивах, списках, копиях и повторной материализации.
Внутри Cell повторяются цвета, декорации, флаги, ссылка на Combiners.

## Прочитанные источники

- S1: OpenTUI, `text-buffer-view.zig`, main (не закреплённый релиз):
  https://raw.githubusercontent.com/anomalyco/opentui/main/packages/native/src/text-buffer-view.zig
  Прочитаны VirtualChunk/VirtualLine, updateVirtualLines, getVirtualLines,
  measureForDimensions. Строки состоят из ссылок на TextChunk и смещений;
  viewport возвращает срез виртуальных строк. Измерение кешируется по epoch/width;
  для временного layout используется arena с retain_capacity. При dirty rebuild
  основной arena в прочитанном снимке сбрасывается с free_all: это НЕ обещание
  полного отсутствия аллокаций. Веб-снимок файла не закреплён commit SHA.
- S2: OpenTUI, `text-buffer.zig`, main:
  https://raw.githubusercontent.com/anomalyco/opentui/main/packages/native/src/text-buffer.zig
  Прочитаны UnifiedTextBuffer и StyledChunk: rope, style spans, content_epoch,
  grapheme/link pools и отдельно ёмкость styled_buffer.
- S3: OpenTUI, `buffer.zig`, main:
  https://raw.githubusercontent.com/anomalyco/opentui/main/packages/native/src/buffer.zig
  Прочитан drawTextBufferInternal: вычисляются firstVisibleLine/lastPossibleLine,
  обходятся только строки между ними. Снимки main у поисковика имеют разные даты
  индексации; вывод относится к приёмам в каждом файле, не к проверенной сборке.
- S4: Ratatui v0.29.0, `src/widgets/reflow.rs`:
  https://raw.githubusercontent.com/ratatui/ratatui/v0.29.0/src/widgets/reflow.rs
  Прочитаны WordWrapper, process_input, replace_current_line, next_line.
  WrappedLine заимствует срез StyledGrapheme; pending_word/whitespace и
  pending_line_pool переиспользуются внутри wrapper. Это локальное переиспользование,
  а не доказательство отсутствия аллокаций между кадрами. Обработка логической
  строки всё ещё может собрать несколько переносов. Символ шире лимита пропускается;
  копировать это поведение в SharpConsoleUI нельзя.
- S5: Will McGugan, автор Textual, «Efficient streaming of Markdown in the terminal»,
  2025-07-24: https://willmcgugan.github.io/streaming-markdown/
  Прочитана статья целиком: сохранение завершённых блоков, обновление последнего,
  повторный разбор хвоста, объединение входящих фрагментов. Заявление sub-1ms —
  авторский результат, не измерение LakeCode и не универсальная гарантия.
- S6: Textual, main, `widgets/_markdown.py`:
  https://raw.githubusercontent.com/Textualize/textual/main/src/textual/widgets/_markdown.py
  Прочитаны MarkdownStream, _token_to_content, append, _update_from_block.
  Content хранит текст и Span; append использует _last_parsed_line и обновляет
  последний блок. При этом source конкатенируется, затем splitlines проходит
  исходник: оптимизация парсера не делает всю операцию строго O(delta).
- S7: Spectre.Console v0.49.1, Segment:
  https://raw.githubusercontent.com/spectreconsole/spectre.console/0.49.1/src/Spectre.Console/Rendering/Segment.cs
  Прочитан исходник: текст + Style, CellCount вычисляет ширину; Split создаёт
  Substring и новые Segment. Это пример run-based модели в C#, не zero-allocation.
  В проекте установлен 0.55.2, commit b18ccb1b00118b7fb81b8fd03fabbdd2324ad8ee
  (проверено по локальному nuspec). Веб-доступ к исходнику именно этой версии
  не удался; поведение 0.49.1 не выдаётся за проверку установленной версии.
- S8: CommonMark 0.31.2, 2024-01-28, §4.7:
  https://spec.commonmark.org/0.31.2/#link-reference-definitions
  Прочитано правило: определение ссылки может следовать после её использования.
  Следовательно, append может менять отображение ранее закрытого блока.
- S9: Microsoft, ArrayPool<T>.Rent, .NET 10:
  https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1.rent?view=net-10.0
  Прочитан контракт: размер не меньше запрошенного; пул переиспользует буферы.
- S10: Microsoft, ArrayPool<T>.Return, .NET 10:
  https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1.return?view=net-10.0
  Прочитаны clearArray и Remarks: после Return владение утрачено, пул может
  удержать массив. Для Cell со ссылкой Combiners важна очистка; публичным List<Cell>
  нельзя подсовывать память, возвращаемую в пул до завершения использования.
- S11: OpenTUI issue #609, авторский bug report, не подтверждённый нами runtime-тест:
  https://github.com/anomalyco/opentui/issues/609
  Отчёт о смешении display offsets и byte offsets при CJK/emoji. Используется
  как указатель риска, а не утверждение о наличии бага в текущей версии.

## Выводы и границы

| ID | Утверждение | Q | Основание | Статус |
|---|---|---|---|---|
| C1 | Перенос может хранить диапазоны исходного текста вместо копий ячеек | Q1 | S1/S2, VirtualChunk и StyleSpan | Факт о реализации; применимость к C# — инженерный вывод |
| C2 | Можно измерять layout отдельно и материализовать только видимые ячейки | Q1/Q3 | S1 measureForDimensions, S3 visible range | Факт о подходе, без гарантии нулевых аллокаций |
| C3 | Переиспользуемые рабочие буферы уменьшают churn | Q4 | S1 measure_arena, S4 pending_line_pool, S9/S10 | Механизм подтверждён, выигрыш LakeCode не измерен |
| C4 | Инкрементальные блоки уменьшают повторный разбор Markdown | Q2 | S5/S6 | Подтверждено по коду; длинный открытый блок остаётся дорогим |
| C5 | Последний блок нельзя считать единственной возможной зависимостью | Q2 | S8 против безусловного обобщения S5 | Нужны зависимости ссылок/расширений или корректный fallback |
| C6 | Смена внутреннего представления позволяет сохранить ScrollToBottom | Q3 | Локальный путь требует актуальной высоты, не массивов Cell | Инженерный вывод |

Не установлены: точный выигрыш нового дизайна, полная инкрементальная поддержка
у установленного Markdig и совместимость любого внешнего движка как замены.
Не выполнялись установка, тесты, чужие build scripts или запуск приложения.
