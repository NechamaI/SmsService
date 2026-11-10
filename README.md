# SMS Service

שירות REST שמקבל בקשות לשליחת SMS, שומר אותן ב־SQL Server, שולח אותן ברקע לספק SMS ומאפשר לשאול על הסטטוס שלהן.

- **מקור האמת לדרישות:** [SPEC.md](SPEC.md), כולל התיקונים שאושרו.
- **תוכנית המימוש:** [docs/2026-11-08-sms-service-plan.md](docs/2026-11-08-sms-service-plan.md).

---

## הרצה

```bash
docker compose up --build
```

עולים שלושה שירותים: `sqlserver`, ‏`mock-provider` ו־`api`. ה־API מחכה ש־SQL Server יהיה healthy, ומריץ את ה־migrations אוטומטית בעלייה.

| מה | כתובת |
|---|---|
| Swagger | http://localhost:8080/swagger |
| API | http://localhost:8080/messages |
| ספק מדומה | http://localhost:8081 (`POST /sms`, ‏`GET /health`) |
| SQL Server | `localhost,1433` (משתמש `sa`) |

### משתני סביבה

| משתנה | ברירת מחדל | משמעות |
|---|---|---|
| `FAIL_RATE` | `0.15` | אחוז הבקשות שהספק המדומה עונה עליהן 503. ‏`0` = תמיד מצליח, ‏`1` = תמיד נכשל |
| `SA_PASSWORD` | `Sms_Service_Passw0rd` | הסיסמה של `sa` ב־SQL Server |

לדוגמה, כדי לראות ניסיונות חוזרים: `FAIL_RATE=0.9 docker compose up --build`.

### בדיקות

```bash
dotnet test
```

70 בדיקות. לא צריך Docker: בדיקות ה־API רצות על SQLite in-memory, עם worker כבוי וספק מזויף.

---

## Endpoints

### `POST /messages`

```bash
curl -X POST http://localhost:8080/messages \
  -H "Content-Type: application/json" \
  -d '{"to":"052-123-4567","body":"Hello"}'
```

**202 Accepted**, עם כותרת `Location: /messages/{id}`:
```json
{ "id": "988e3fbb-c470-45c3-05e5-08df3dde9286", "status": "Pending" }
```

**400**, ‏`application/problem+json`, עם שגיאה לכל שדה:
```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "to": ["Invalid phone number. Expected an Israeli mobile number, e.g. 052-1234567 or +972521234567."]
  }
}
```

- **`to`:** סלולר ישראלי, ‏`05XXXXXXXX`, ‏`+9725XXXXXXXX`, ‏`9725XXXXXXXX` או `009725XXXXXXXX`. מותר להשתמש ברווחים, במקפים ובסוגריים. המספר נשמר מנורמל: `+972521234567`.
- **`body`:** לא ריק. עד 3 חלקי SMS: ‏459 תווים ב־GSM7, ‏201 ב־UCS2 (עברית או אימוג'י).

> **הערה ל־Windows:** ב־Git Bash, עברית שמועברת ל־`curl` כארגומנט לא מגיעה כ־UTF-8 תקין. כדי לשלוח עברית מ־curl, שמרו את הגוף בקובץ ושלחו עם `--data-binary @file.json`, או השתמשו ב־Swagger.

### `GET /messages/{id}`

```bash
curl http://localhost:8080/messages/988e3fbb-c470-45c3-05e5-08df3dde9286
```

**200:**
```json
{
  "id": "988e3fbb-c470-45c3-05e5-08df3dde9286",
  "to": "+972521234567",
  "body": "שלום, זו הודעת בדיקה",
  "status": "Sent",
  "attempts": 1,
  "lastError": null,
  "segments": 1,
  "encoding": "UCS2",
  "createdAt": "2026-11-08T21:24:05.8284215Z",
  "updatedAt": "2026-11-08T21:24:07.3045844Z",
  "sentAt": "2026-11-08T21:24:07.3045844Z"
}
```
**404** אם ההודעה לא קיימת.

### `GET /messages?status=Failed&page=1&pageSize=50`

```bash
curl "http://localhost:8080/messages?status=Failed&page=1&pageSize=50"
```

**200:**
```json
{ "items": [ { "id": "...", "status": "Failed", "attempts": 1, "lastError": "HTTP 400: {\"error\":\"Recipient blocked\"}", "...": "..." } ],
  "page": 1, "pageSize": 50, "total": 1 }
```

- `status` אופציונלי ולא תלוי רישיות. ערך לא חוקי או מספרי (`status=1`) מחזיר 400.
- `page >= 1`, ‏`1 <= pageSize <= 200` (ברירת מחדל 50). ערך מחוץ לטווח מחזיר 400, ולא נחתך בשקט.
- מיון: החדשות קודם.

---

## החלטות תכנון

### 202 ו־worker ברקע
`POST` שומר את ההודעה כ־`Pending` ומחזיר מיד 202. ‏`SendingWorker` (`BackgroundService`) שולף כל שנייה עד 20 הודעות שהגיע זמנן ושולח אותן.
כך הלקוח לא מחכה לספק, ואיטיות או נפילה של הספק לא מפילות את הבקשה. ההודעה כבר שמורה, וה־worker ינסה שוב.

### EF Core ולא Stored Procedures
EF Core עם migrations שרצות בעלייה: מבנה ה־DB נשמר בקוד ובגיט, ו־`docker compose up` מקבל מסד ריק ויוצר אותו לבד.
אין כאן לוגיקה כבדה בצד ה־DB שמצדיקה Stored Procedures. השאילתה של ה־worker פשוטה ונתמכת באינדקס `(Status, NextAttemptAt)`.

### סטטוסים וניסיונות חוזרים
```
Pending ──> Sending ──> Sent
   ^           │
   └───────────┤  (שגיאה זמנית, פחות מ־4 ניסיונות)
               └──> Failed
```
- `Attempts` עולה בכל קריאה לספק.
- **שגיאה זמנית** (timeout, שגיאת רשת, 408, ‏429, ‏5xx): חזרה ל־`Pending` עם backoff של 2, ‏10 ו־30 שניות. בניסיון הרביעי: `Failed`.
- **שגיאה קבועה** (שאר ה־4xx): ‏`Failed` מיד.
- `Sent` אומר שהספק **קיבל** את ההודעה, לא שהיא נמסרה. לכן אין `Delivered`.
- ה־worker מסמן `Sending` ו**שומר לפני** הקריאה לספק. בעלייה, הודעות שנשארו ב־`Sending` (השירות נפל באמצע) חוזרות ל־`Pending`.

### GSM7 ו־UCS2
מגבלה קבועה של 160 תווים שגויה לעברית. הקידוד נקבע לפי התוכן, ומספר החלקים מחושב לפיו:

| קידוד | חלק בודד | חלק בהודעה מפוצלת | מקסימום (3 חלקים) |
|---|---|---|---|
| GSM7 | 160 | 153 | 459 |
| UCS2 | 70 | 67 | 201 |

תו מטבלת ההרחבה של GSM7 (`€`, ‏`{`, ‏`}` וכו') נספר כ־2. ב־UCS2 אימוג'י נספר כ־2. מספר החלקים נשמר בעמודה `Segments`, כי הספק מחייב לפי חלק.

### בדיקות
- **TDD** ללוגיקה הקריטית: ‏`PhoneNumberNormalizer`, ‏`SmsLengthCalculator`, ‏`MessageSender`. בדיקות בגבולות (160/161, ‏70/71, ‏459/460, ‏201/202), עם שעון וספק מזויפים.
- **בדיקות אינטגרציה ל־API:** ‏`WebApplicationFactory` על SQLite in-memory. בודקות קודי תגובה, ProblemDetails, סינון ועימוד, ומקרי קצה כמו `{}` (400 ולא 500) ו־`status=1`.
- **`HttpSmsProvider`:** איך כל קוד HTTP מתורגם לשגיאה זמנית או קבועה.

---

## בדיקה ידנית מקצה לקצה

הורץ ב־`docker compose up --build` מול SQL Server אמיתי.

| תרחיש | תוצאה |
|---|---|
| הודעה בעברית | `Pending`, ותוך כשנייה וחצי `Sent`. ‏`encoding: UCS2`, ‏`segments: 1`, ‏`providerMessageId` מולא, והטקסט נשמר נכון |
| מספר שמסתיים ב־`999` | `Failed` אחרי ניסיון אחד, ‏`lastError: HTTP 400: {"error":"Recipient blocked"}` |
| `FAIL_RATE=0.9`, ‏5 הודעות | הניסיונות עלו עם ה־backoff. ‏4 הודעות הגיעו ל־`Failed` אחרי 4 ניסיונות, ואחת ל־`Sent` בניסיון הרביעי. בלוג של הספק: 503 בשעות 21:24:40, ‏:44 ו־:56, ו־200 ב־21:25:27, כלומר מרווחים של כ־2, ‏10 ו־30 שניות |
| טלפון לא תקין (`03-1234567`) | 400, ‏`application/problem+json`, שגיאה תחת `to` |
| 202 תווים בעברית / 201 תווים | 400 עם "Message is 202 characters long (UCS2); the maximum is 201." / ‏202 |
| `GET /messages?status=Failed` | רק הודעות `Failed` |
| `docker compose restart api` עם 8 הודעות ב־`Sending` | אחרי העלייה: 0 ב־`Sending`. בלוג: "Returned 7 interrupted Sending messages to Pending". כל ההודעות סיימו (`Sent` או `Failed`) |
| עימוד (`page=2&pageSize=5`) | `page: 2`, ‏`pageSize: 5`, ‏`total` נכון |

---

## מגבלות ידועות

- **שליחה כפולה אפשרית:** אחרי timeout (הספק אולי כבר קיבל את ההודעה) או אחרי קריסה באמצע שליחה. ה־`Id` שלנו נשלח לספק בשדה `reference`, כבסיס ל־idempotency.
- **מופע יחיד:** שני workers יכולים לשלוף את אותה הודעה. ראו "מה הייתי מוסיף".
- **שליחה סדרתית:** במקרה הגרוע, סבב של 20 הודעות × timeout של 5 שניות = 100 שניות.
- **תו הרחבה של GSM7 שנופל על הגבול בין שני חלקים** לא מטופל. הספירה יכולה לטעות בחלק אחד במקרה כזה.
- **SQLite בבדיקות ולא SQL Server:** הבדיקות יכולות לעבור על SQLite ולהיכשל על SQL Server (טיפוסים, אורך עמודות). מכוסה כאן בבדיקה הידנית. הפתרון הנכון הוא Testcontainers.
- **JSON שבור** מחזיר 400 עם הודעת השגיאה של ASP.NET (מפתחות `request` ו־`$.body`), ולא הודעה מותאמת.

---

## שימוש ב־AI

הפרויקט פותח עם Claude Code (Claude Opus 5.5). תהליך העבודה:
1. brainstorming כדי לאתגר את ה־SPEC ולמצוא מקרי קצה, ואז תיקונים ל־SPEC.
2. תוכנית מימוש כתובה ([docs/](docs/)).
3. מימוש שלב אחרי שלב: TDD ללוגיקה הקריטית, ובסוף כל שלב סקירה ואישור שלי לפני commit.

כל הקוד נסקר על ידי. החלטות התכנון, ההנחות והתיקונים ל־SPEC הם שלי.

---

## מה הייתי מוסיף עם עוד זמן (חלק ב')

- **עובדים:** טבלאות `Employees` ו־`EmployeePhones` (קשר רבים־לרבים) ו־endpoint לשליחה לעובד. המודל מתוכנן ב־[SPEC סעיף 9](SPEC.md).
- **Idempotency key** על `POST /messages`, כדי שלקוח שמנסה שוב לא ייצור הודעה כפולה.
- **דוחות מסירה (DLR)** מהספק, וסטטוס `Delivered`.
- **ספק שני עם failover** כשהספק הראשי לא זמין.
- **תור חיצוני** (למשל RabbitMQ או Azure Service Bus) במקום polling על ה־DB, וכמה workers. או, עם ה־DB, נעילת שורות עם `UPDLOCK, READPAST`.
- **שליחה במקביל** עם הגבלה (`Parallel.ForEachAsync`).
- **Testcontainers:** בדיקות אינטגרציה מול SQL Server אמיתי.
- **ניטור:** מטריקות (הודעות לפי סטטוס, זמן עד שליחה, אחוז כשלים), ‏health endpoints ל־API ול־DB.
- **ממשק משתמש** פשוט לשליחה ולצפייה בסטטוס.
