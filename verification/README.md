# Проверки Creative Relab

- [PC Care 1.1.2](pc-care-1.1.2.json): 27 внутренних и 34 интерфейсных проверки
- [PC Guard 3.4.1](pc-guard-3.4.1-application.json): 298 проверок оболочки; [13 скриптов PowerShell](pc-guard-3.4.1-powershell.json) без ошибок парсера
- Драйвер PC Guard: [40 kernel-проверок](pc-guard-kernel-40.json), [42 с Driver Verifier](pc-guard-verifier-42.json); без производственной подписи и без загрузки на основной Windows
- [PC Tune: все 42 пункта](PC-TUNE-RESULTS-RU.md): 30 применены и откатились, 12 неприменимы на стенде; [1799 проверок UI](pc-tune-1.0.1-ui.json) и [46 выбранных тестов](pc-tune-selected-tests.json)
- [Три распаковщика](portable-unpackers.json): по 8 проверок; сохранение Data, резерв и отказ записи вне D
- [Обратная загрузка шести ZIP/EXE](github-download-roundtrip.json): реальные загрузки GitHub и совпадение SHA-256
- [Каталог на пяти ширинах](software-live-layout.json): 24 проверки без ошибок
- [Публикация каталога](software-deployment.json): 58 существующих файлов без изменений, бинарные загрузки находятся на GitHub

Результаты относятся к указанным версиям и сценариям. Сырые сведения о пользовательских файлах, секреты и виртуальные диски не публикуются.
