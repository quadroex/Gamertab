# Gamertab

Вебпроєкт для пошуку ігор, персональних рекомендацій і ведення власної ігрової бібліотеки.

Грицина Антон Олександрович, ТТП-32.

- [Відкрити сайт](https://gamertab-917646089327.us-east1.run.app/)
- [Розбаловка та довідка до захисту (Gamertab-defense.pdf)](https://github.com/quadroex/Gamertab/blob/main/defense/Gamertab-defense.pdf)
- [Збірка, тести й автоматичне розгортання](https://github.com/quadroex/Gamertab/actions/workflows/deploy.yml)

## Пункти для перевірки

**Заявлено 32 пункти із сумарною вагою 88 балів.** У PDF наведено кожен пункт, його вагу та реалізацію. Остаточне зарахування пунктів і підсумкову оцінку визначає викладач.

| Категорія | Пунктів | Балів |
|---|---:|---:|
| Common | 18 | 51 |
| Backend | 8 | 22 |
| Frontend | 3 | 7 |
| QA | 3 | 8 |
| Разом | 32 | 88 |

PDF містить результати повної перевірки від 03.11.2026. Результати перевірки поточних комітів доступні в GitHub Actions.

## Що можна перевірити на сайті

- **Browse та сторінка гри**. Пошук IGDB, популярні ігри, платформи, рейтинги, скриншоти, трейлер, доступні ціни й новини Steam. Search saved games використовує повнотекстовий пошук Lucene.NET.
- **Акаунт та My library**. Реєстрація, вхід через Google або пароль, статуси проходження, оцінки, нотатки та експорт бібліотеки в Markdown.
- **Collections**. Створення колекцій, автодоповнення пошуку ігор, обкладинка з URL або файлу, попередній перегляд файлу та приватне сховище.
- **For you та Statistics**. Персональні рекомендації з поясненнями. Statistics доступна з My library після демонстраційного Upgrade до Premium в Account.
- **Play journal та Activity**. Блоковий редактор, перетягування, автозбереження, експорт Markdown, синхронізація й анімація змін у двох браузерах одного акаунта. Activity дозволяє порівняти SignalR, WebSocket, long polling та adaptive polling.
- **Assistant та Feedback**. Gemini з інструментами читання бібліотеки й рекомендацій. Звернення до підтримки створюються в HubSpot CRM, їхній статус читається назад на сайті.
- **SEO та аналітика**. Метадані, sitemap.xml, robots.txt і Google Analytics лише після згоди через Analytics preferences.

Для перевірки можна створити власний акаунт. Усі акаунти спочатку мають Standard. Демонстраційний Upgrade не проводить справжню оплату. Бібліотеки, колекції та щоденники доступні лише власнику.

## Технології та код

C#, .NET 10, ASP.NET Core MVC, Razor, PostgreSQL, EF Core, Identity, SignalR, Lucene.NET, Bootstrap, Select2 та Chart.js.

- [Gamertab](Gamertab) містить Controllers, Models, Data, Services, Views і wwwroot.
- [Gamertab.Tests](Gamertab.Tests) містить інтеграційні та браузерні тести на C# із Playwright і Coverlet.
- [api-tests](api-tests) містить колекцію Postman із 40 запитами та 65 перевірками.
- [functions/LibraryExport](functions/LibraryExport) містить окрему приватну HTTP-функцію експорту бібліотеки.
- [Dockerfile](Dockerfile), [compose.yaml](compose.yaml) і [workflow](.github/workflows/deploy.yml) задають локальне середовище та розгортання.

Публічний сайт працює в Google Cloud Run із PostgreSQL Neon, приватним Google Cloud Storage та Secret Manager. GitHub Actions перевіряє код і автоматично розгортає main. Локально PostgreSQL та Azurite працюють у Docker.

## Локальний запуск

Потрібні .NET 10 SDK, PowerShell і Docker. На цьому комп'ютері Docker працює через WSL2 Ubuntu-24.04. Команди виконуються з кореня репозиторію.

```powershell
.\dev.ps1 run
```

Сайт відкривається за адресою http://localhost:5180. Скрипт запускає PostgreSQL та Azurite й готує локальні налаштування. Ключі інтеграцій задаються у приватному `.env`, який не входить до Git.

| Інтеграція | Поля у .env |
|---|---|
| Живий каталог IGDB | IGDB_CLIENT_ID, IGDB_CLIENT_SECRET |
| Google OAuth | GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET |
| Gemini | GEMINI_API_KEY |
| HubSpot CRM | HUBSPOT_ACCESS_TOKEN |

Для Google OAuth локальний redirect URI має бути `http://localhost:5180/signin-google`. Зупинка контейнерів зі збереженням даних виконується командою `.\dev.ps1 down`.

## Перевірки

```powershell
.\dev.ps1 test
.\dev.ps1 browser-test
.\dev.ps1 api-test
.\dev.ps1 queue-test
.\dev.ps1 blob-test
.\dev.ps1 connectivity-test
```

`test` запускає перевірки без браузера, `browser-test` запускає Playwright і вимірює покриття C# через Coverlet. Для браузерних тестів потрібен Microsoft Edge, для `api-test` потрібен Postman CLI. `queue-test` і `blob-test` використовують справжній локальний Azurite. `connectivity-test` порівнює затримку й трафік чотирьох способів отримання подій.

Перевірки використовують тимчасові бази й сховища та не змінюють основну бібліотеку. Зовнішні HTTP-відповіді в автоматичних тестах контрольовані. Живі інтеграції перевіряються окремо. Підсумки аудиту та вимірювань збережено в [defense/checks.json](defense/checks.json) і [defense/connectivity.json](defense/connectivity.json).

Для перевірки локального MCP потрібні наявний локальний акаунт, запущена база та Codex CLI або встановлений застосунок Codex.

```powershell
.\dev.ps1 mcp-client -AccountEmail you@example.com
```

Адресу слід замінити на пошту локального акаунта. Команда `/mcp` у клієнті показує підключення. Доступні `search_games`, `get_library`, `list_collections` та `add_to_library`. Це окремий локальний stdio-сервер, прив'язаний до вибраного акаунта.

## Межі реалізації

- Локальна та хмарна бази незалежні. Фонова черга новин працює локально через Azurite. У хмарі новини читаються під час відкриття гри.
- Вебчат і MCP шукають лише збережений каталог. Нові ігри імпортуються через Browse. Зовнішні API мають власні квоти й можуть бути недоступними.
- Metacritic, ціна Steam у USD і час проходження показуються лише за наявності даних. Інші магазини та мапа не заявляються.
- Cloud Run обмежений одним екземпляром. Хостинг використовує активований Google Cloud trial, тому його доступність залежить від строку й залишку кредитів.
