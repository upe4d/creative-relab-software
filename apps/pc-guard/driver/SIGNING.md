# PC Guard: подпись и испытания драйвера

Проверено по документации Microsoft 6 ноября 2026 года. Этот документ не означает, что драйвер подписан или проверен в ядре.

## Текущее состояние

Собран x64 PCGuardFilter.sys, протокол 4. Строгие W4/WX, GS, NX, ASLR, FORCE_INTEGRITY. Символы PCGuardFilter.pdb лежат рядом. На основной Windows служба не создана, фильтр не загружен. IObit, Defender, Secure Boot и режим тестовой подписи не переключались.

В двух локальных хранилищах CurrentUser/My и LocalMachine/My не найден Code Signing сертификат с закрытым ключом. Наличие облачного сертификата или учётной записи Partner Center этим не проверялось.

## Что требуется для обычного выпуска

1. Реальный издатель и регистрация в Microsoft Windows Hardware Developer Program / Hardware Dev Center. Для dashboard требуется действующий EV Code Signing сертификат, связанный с учётной записью. Допустимость выдачи конкретному заявителю проверяется у удостоверяющего центра; цена и возможность покупки здесь не установлены.
2. Проверенная сборка, испытания в отдельной Windows, Driver Verifier и тесты совместимости. Рекомендованный Microsoft путь для выпуска: HLK/WHCP и подпись через dashboard. Актуальная документация называет attestation вариантом только для испытаний. Attestation не является Windows Certified и не даёт обычную публикацию в Windows Update.
3. Для публично распространяемого minifilter — назначенная Microsoft высота в соответствующей группе. Планируемая группа FSFilter Anti-Virus, диапазон 320000–329999; номер самостоятельно не присваивается публичному продукту. Для внутренних ранних испытаний Microsoft разрешает тестовую высоту без заявки. Высоту заявки нужно запрашивать после стабилизации функций. Срок обработки — до 30 рабочих дней. Черновик: altitude-request.txt; сообщение никому не отправлено.
4. Пакет по требованиям dashboard: SYS, INF, PDB и CAT. Требования сверяются непосредственно перед отправкой. CAB подписывается зарегистрированным сертификатом SHA-256; Microsoft возвращает подписанные файлы. Одна локальная EV-подпись SYS не заменяет подпись Microsoft для современного штатного kernel policy.
5. Проверка возвращённого SYS официальным signtool verify /kp, повторные испытания именно возвращённого файла, затем запуск с правами администратора. Сейчас Check-Release.ps1 и Install-Driver.ps1 отказывают в установке, потому что обязательные условия отсутствуют.

Закрытые ключи, PFX, пароль токена и данные учётной записи не нужно отправлять в чат или включать в архив программы. Регистрация, покупка, подпись и отправка заявки пока не выполнялись.

## Ограничение «всё на D»

Исходники, инструменты, EXE/SYS/PDB, пользовательские данные и файлы стенда хранятся на D. Для текущей версии приложение обновляется только в D:\PC Guard.

Стандартный современный INF minifilter использует системный Driver Store (DIRID 13). Его установка в основной Windows создаёт системные файлы на системном томе. Такая установка НЕ выполнена. Старый экспериментальный Install-Driver.ps1 создаёт demand-службу с SYS на D, но это не подтверждение соответствия современному INF/WHCP-развёртыванию. Он дополнительно заблокирован до подтверждённого пакета. Финальный INF/каталог и соответствие упаковки Microsoft ещё не подготовлены и не проверены. Нельзя обещать стандартную установку драйвера буквально без единой системной записи на C.

## Стенд и тестовая подпись

Файлы подготовки: D:\Codex\work\PCGuard-lab. Готовой гостевой Windows пока нет. Переносимый QEMU распакован без установки в систему. Windows 11 Enterprise Evaluation загружается с Microsoft; полная загрузка и SHA фиксируются отдельным статусом, незаконченный ISO нельзя использовать. Загруженная Windows-сборка QEMU не предоставляет -tpmdev; обычный Windows 11-стенд с vTPM ещё не готов. Это препятствие не обходилось изменениями основной Windows.

Тестовую самоподпись, доверие тестовому сертификату, Test Signing и Driver Verifier можно настраивать только внутри отдельной гостевой Windows с подтверждённым UUID. Основная Windows для этих опытов не используется. Самоподпись гостя бесплатна, но не является подписью для обычного распространения.

## Открытый блокер отображений

AcquireForSectionSynchronization по контракту WDK нельзя использовать как общий ACCESS_DENIED gate: для SyncTypeCreateSection описан отказ только при нехватке ресурсов. Поэтому текущий callback только инвалидирует кеш. Запрет старых writable mappings при commit и будущих пишущих открытий не доказывает защиту от передачи разрешённого handle другому процессу. Нужны отдельное решение и проверки. Ранний SHA-перехват открытия также не покрывает все старые читающие handles. Полный DLL/mapping-контроль не завершён.

Источник: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/flt-parameters-for-irp-mj-acquire-for-section-synchronization

## Что обязательно проверить в ядре

- Отказ запуска безопасного искусственного EXE, чей SHA внесён в отдельную тестовую политику; файл-маркер первого потока не появляется. Разрешённый EXE запускается.
- Создание исполняемой секции EXE/DLL; изменение после кеширования, ошибки чтения, размер/лимит/таймаут, смена политики.
- Новая запись, truncate/overwrite, append, удаление, rename, hardlink и ссылки, созданные до включения.
- Запись через отображение памяти; старый writable mapping при уже закрытых handle/section должен сорвать включение политики.
- Старые открытые пишущие дескрипторы, ADS, hardlinks вне выбранной папки; дерево с reparse point должно отказаться от включения.
- Разрешённый редактор после нового запуска; изменённый EXE не получает права. Перенос защищённой папки/родителя, hardlink и вынос данных редактором не разрешаются.
- Неудачный commit/закрытие устройства отменяет pending и сохраняет старую политику. Отключение/перезагрузка/низкая память, многопоточные гонки, unload.
- NTFS/ReFS отдельно, Driver Verifier, целевая Windows 11 и взаимодействие с действующими антивирусами на стенде. Основная машина не является тестовым стендом.

Код перехвата сам по себе не доказывает эти свойства. До выполнения испытаний это исследовательская сборка, а не законченная защита от шифровальщиков. Контроль камеры для произвольных EXE этим файловым драйвером не реализован.

## Первоисточники

- https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-reqs
- https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings
- https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/minifilter-altitude-request
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/load-order-groups-and-altitudes-for-minifilter-drivers
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/creating-an-inf-file-for-a-minifilter-driver
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/using-driver-verifier
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-mmdoesfilehaveuserwritablereferences
