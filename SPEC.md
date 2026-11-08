# SPEC – שירות שליחת SMS

> המסמך הזה הוא ה־SPEC שלפיו נבנה הפיתוח (בעזרת Claude Code). הוא מוגש כחלק מהמבחן, כנדרש.

## 1. מטרה

שירות REST שמקבל בקשות לשליחת הודעות SMS, שומר אותן במסד נתונים, שולח אותן ברקע לספק SMS חיצוני ומאפשר לשאול על הסטטוס שלהן.
המערכת רצה מקומית בפקודה אחת: `docker compose up --build`.

## 2. הנחות יסוד

| # | הנחה | נימוק |
|---|------|-------|
| 1 | **השדה `to` הוא מספר טלפון ולא מזהה עובד.** | הדרישה היא ולידציה על "מספר טלפון", ולעובד אין מזהה חיצוני. מספר טלפון לא מזהה עובד באופן חד־ערכי (מספר יכול להיות משותף לכמה עובדים), ולכן ההודעה ממוענת למספר. |
| 2 | עובד מזוהה לפי הצירוף (שם פרטי, שם משפחה, כתובת). לעובד יש מספר טלפונים, וטלפון יכול להופיע אצל כמה עובדים. | כך נכתב במשימה. בגרסה הזו לא נבנה endpoint לשליחה לעובד. המודל המוצע מתואר בסעיף 9, ונימוק הדחייה מופיע ב־README. |
| 3 | רק מספרי **סלולר ישראליים** נתמכים. | המערכת שולחת לעובדי ארגון ישראלי. קל להרחיב בהמשך. |
| 4 | **לא קיבלנו את השרת המדומה של הספק**, ולכן בנינו מדמה משלנו (`mock-provider`). הגישה לספק עוברת דרך ממשק `ISmsProvider`. | החלפה לשרת המדומה האמיתי היא מימוש חדש של הממשק או שינוי של כתובת והמרת פורמט, בלי לגעת בלוגיקה. |
| 5 | הספק מאשר **קבלה** של ההודעה, לא מסירה לנמען. | לכן הסטטוס הסופי הוא `Sent` ולא `Delivered`. דוחות מסירה (DLR) מתוכננים להמשך (חלק ב'). |
| 6 | מופע יחיד של ה־API (וגם של ה־worker). | פשטות. ההשלכות על עומסים נדונות בחלק ב'. |
| 7 | אין אימות משתמשים ואין Deploy. | כך נכתב במשימה. |

## 3. טכנולוגיות

- **‏.NET 10 (LTS)**, ASP.NET Core Web API עם Controllers.
- **SQL Server 2022** בקונטיינר Docker.
- **EF Core** עם migrations שרצות אוטומטית בעלייה.
  - כי SQL Server יכול לדווח healthy לפני שהוא מקבל חיבורים, יש `EnableRetryOnFailure` ולולאת retry קצרה סביב ה־migration.
  - נימוק: מהירות פיתוח במסגרת 4 שעות.
  - Stored Procedures לא נדרשים כאן, כי אין לוגיקה כבדה בצד ה־DB. ההחלטה מתועדת ב־README.
- **xUnit** לבדיקות.
- **Swagger** לבדיקה ידנית.
- **docker-compose** עם השירותים `sqlserver`, `mock-provider` ו־`api`.

## 4. מבנה הפרויקט

```
/src/SmsService.Api
    Controllers/MessagesController.cs
    Domain/Message.cs, MessageStatus.cs
    Validation/PhoneNumberNormalizer.cs, SmsLengthCalculator.cs
    Sending/ISmsProvider.cs, HttpSmsProvider.cs, MessageSender.cs, SendingWorker.cs
    Data/AppDbContext.cs, Migrations/
    Program.cs
/src/MockSmsProvider          (minimal API קטן)
/tests/SmsService.Tests
docker-compose.yml
README.md
SPEC.md
```

## 5. מודל נתונים

טבלה `Messages`:

| עמודה | סוג | הערות |
|-------|-----|-------|
| Id | uniqueidentifier, PK | נוצר בשרת |
| To | nvarchar(16) | מנורמל לפורמט E.164, למשל `+972521234567` |
| Body | nvarchar(1000) | |
| Encoding | varchar(10) | `GSM7` או `UCS2` |
| Segments | int | מספר חלקי ה־SMS |
| Status | varchar(20) | ראו סעיף 7 |
| Attempts | int | ברירת מחדל 0 |
| NextAttemptAt | datetime2 | מתי ה־worker רשאי לנסות שוב |
| LastError | nvarchar(500), nullable | |
| ProviderMessageId | nvarchar(100), nullable | |
| CreatedAt, UpdatedAt | datetime2 | UTC |
| SentAt | datetime2, nullable | |

אינדקסים:
- `(Status, NextAttemptAt)` עבור ה־worker.
- `(Status, CreatedAt DESC)` עבור הרשימה.

## 6. API

### `POST /messages`

```json
{ "to": "052-123-4567", "body": "שלום" }
```

- **202 Accepted** + כותרת `Location: /messages/{id}`:
  ```json
  { "id": "…", "status": "Pending" }
  ```
- **400** עם `ProblemDetails` (שגיאה לכל שדה) כשהוולידציה נכשלת.
- הנימוק ל־202: השליחה בפועל נעשית ברקע. הלקוח לא מחכה לספק, וכשל או איטיות של הספק לא מפילים את הבקשה.

### `GET /messages/{id}`

- **200**:
  ```json
  { "id", "to", "body", "status", "attempts", "lastError", "segments", "encoding", "createdAt", "updatedAt", "sentAt" }
  ```
- **404** אם ההודעה לא קיימת.

### `GET /messages?status=Failed&page=1&pageSize=50`

- `status` אופציונלי. ערך לא חוקי מחזיר 400.
- `status` לא תלוי רישיות (`pending` תקין). ערך מספרי (`status=1`) מחזיר 400.
- `pageSize` ברירת מחדל 50, מקסימום 200.
- `page < 1`, `pageSize < 1` או `pageSize > 200` מחזירים 400. הערך לא נחתך בשקט לגבול הקרוב.
- מיון: החדשות קודם.
- תגובה:
  ```json
  { "items": [...], "page", "pageSize", "total" }
  ```

## 7. סטטוסים ומחזור חיים

```
Pending ──(worker לוקח)──> Sending ──(הספק אישר)──> Sent
   ^                         │
   └──(שגיאה זמנית, נשארו ניסיונות)──┘
                             └──(שגיאה קבועה / נגמרו ניסיונות)──> Failed
```

- **`Attempts` גדל בכל קריאה לספק**, בלי קשר לתוצאה (הצלחה, שגיאה זמנית או קבועה).
- **שגיאה זמנית:** timeout, שגיאת רשת, HTTP 408, 429 או 5xx, וכן כל חריגה לא צפויה מהספק. מחזירים ל־`Pending` וקובעים `NextAttemptAt = now + backoff`, לפי ערך `Attempts` אחרי ההגדלה:
  - `Attempts = 1`: 2 שניות.
  - `Attempts = 2`: 10 שניות.
  - `Attempts = 3`: 30 שניות.
- **שגיאה קבועה:** HTTP 4xx (פרט ל־408 ו־429). עוברים מיד ל־`Failed`.
- **מקסימום 4 ניסיונות.** שגיאה זמנית כש־`Attempts >= 4` מעבירה ל־`Failed`.
- `LastError` נחתך ל־500 תווים.
- **timeout נחשב שגיאה זמנית**, אף שייתכן שהספק כבר קיבל את ההודעה.
  - הסיכון הוא שליחה כפולה.
  - מזהה ההודעה שלנו נשלח לספק בשדה `reference`, כבסיס ל־idempotency בחלק ב'.
- **בעלייה של השירות:** הודעות שנשארו ב־`Sending` (קריסה באמצע) חוזרות ל־`Pending`.
  - הסיכון הוא שליחה כפולה.
  - זה מקובל בגרסה הזו ונדון בחלק ב'.

## 8. ולידציה

### מספר טלפון (`PhoneNumberNormalizer`)

- מסירים רווחים, מקפים וסוגריים.
- פורמטים מקובלים:
  - `05XXXXXXXX`
  - `+9725XXXXXXXX`
  - `9725XXXXXXXX`
  - `009725XXXXXXXX`
- מנרמלים ל־`+9725XXXXXXXX`. אחרי `05` צריכות להופיע בדיוק 8 ספרות.
- רק ספרות ASCII ‏(`0`–`9`). ספרות בכתב אחר, למשל ערבית־הודית, נדחות.
- כל קלט אחר מחזיר 400 עם הודעה ברורה. זה כולל:
  - אפס אחרי קידומת המדינה (`+9720521234567`).
  - נקודות כמפריד (`052.123.4567`).

### תוכן ההודעה (`SmsLengthCalculator`)

- ריק או רווחים בלבד מחזיר 400. אחרת התוכן נשמר כמו שהוא, בלי trim.
- **הקידוד נקבע לפי התוכן:**
  - אם כל התווים שייכים ל־GSM-7 basic charset (כולל טבלת ההרחבה, שבה כל תו נספר כ־2), הקידוד הוא `GSM7`.
  - אחרת, למשל בעברית, הקידוד הוא `UCS2`.
- **מגבלות:**

  | קידוד | חלק בודד | חלק בהודעה מפוצלת | מקסימום |
  |---|---|---|---|
  | GSM7 | 160 | 153 | 3 חלקים = 459 |
  | UCS2 | 70 | 67 | 3 חלקים = 201 |

- **ספירת האורך:**
  - ב־GSM7 סופרים septets: תו בסיסי = 1, תו מטבלת ההרחבה = 2.
  - ב־UCS2 סופרים יחידות UTF-16 (`string.Length`), כך ש־emoji נספר כ־2.
  - מספר החלקים: `אורך <= חלק בודד ? 1 : ceil(אורך / חלק בהודעה מפוצלת)`.
  - פישוט מתועד: לא מטפלים בתו הרחבה שנופל על הגבול בין שני חלקים.
- חריגה מהמקסימום מחזירה 400, עם האורך שנמדד והמקסימום המותר.
- נימוק: מגבלה של 160 תווים בלבד שגויה לעברית. גם עלות ההודעה תלויה במספר החלקים.

## 9. עובדים (מודל מוצע, לא ממומש בגרסה הזו)

- טבלה `Employees`:
  - `Id` (מפתח פנימי), `FirstName`, `LastName`, `Address`.
  - אינדקס ייחודי על (FirstName, LastName, Address).
- טבלה `EmployeePhones`:
  - `Id`, `EmployeeId` (FK), `Phone`.
  - אינדקס ייחודי על (EmployeeId, Phone) בלבד. הטלפון לבדו לא ייחודי.
- הרחבה עתידית: `POST /messages` עם `{ "employee": {firstName, lastName, address}, "body" }`, ששולח לכל המספרים של העובד ומחזיר רשימת מזהים.

## 10. רכיבי השליחה

- **`ISmsProvider.SendAsync(reference, to, body, ct)`** מחזיר `ProviderResult { Success, IsTransient, ProviderMessageId, Error }`. ‏`reference` הוא ה־`Id` של ההודעה.
- **`HttpSmsProvider`:**
  - עובד מול `Provider:BaseUrl` מתוך ה־config, עם timeout של 5 שניות.
  - ממפה תגובות HTTP ל־`ProviderResult`.
- **`MessageSender`:** לוגיקה טהורה שקובעת את הסטטוס הבא לפי התוצאה. בלי תלות ב־DB, מקבל `TimeProvider`. זה החלק הקריטי שנבדק.
- **`SendingWorker : BackgroundService`:**
  - כל שנייה שולף עד 20 הודעות `Pending` שהגיע זמנן (`NextAttemptAt <= now`).
  - מסמן אותן `Sending` ושומר ב־DB **לפני** קריאה כלשהי לספק. בלי זה, ההחזרה ל־`Pending` בעלייה לא אמינה.
  - שולח אחת־אחת דרך `MessageSender` ושומר אחרי כל הודעה.
  - שגיאה באיטרציה נרשמת ללוג, וה־worker ממשיך לרוץ.
  - גודל האצווה ניתן להגדרה (`Worker:BatchSize`), וה־worker ניתן לכיבוי (`Worker:Enabled`, משמש בבדיקות).
  - פשרה מתועדת: במקרה הגרוע (20 הודעות × timeout של 5 שניות) האצווה חוסמת את ה־worker לכ־100 שניות.

## 11. ספק מדומה (`MockSmsProvider`)

- **`POST /sms`** עם `{ "to", "text", "reference" }`. ה־`reference` נרשם ללוג:
  - **200** עם `{ "id": "<guid>" }`.
  - עיכוב אקראי של 100–800ms.
  - בהסתברות `FAIL_RATE` (ברירת מחדל 0.15) מחזיר **503**.
  - מספר שמסתיים ב־`999` מחזיר **400** (נמען חסום). כך אפשר להדגים כשל קבוע.
- **`GET /health`** מחזיר 200.

## 12. בדיקות אוטומטיות

- `PhoneNumberNormalizer`: פורמטים תקינים מנורמלים נכון, ופורמטים לא תקינים נדחים (קווי, קצר, ארוך, אותיות).
- `SmsLengthCalculator`:
  - אנגלית בגבולות 160/161 ו־459/460.
  - עברית בגבולות 70/71 ו־201/202.
  - תו מטבלת ההרחבה (`€`, `{`) נספר כ־2.
  - תו עברי אחד הופך הודעה ל־UCS2.
- `MessageSender` (עם fake provider ו־fake time):
  - הצלחה מובילה ל־`Sent`, עם `SentAt` ו־`ProviderMessageId`.
  - שגיאה זמנית מחזירה ל־`Pending` עם backoff נכון.
  - שגיאה זמנית בניסיון הרביעי מובילה ל־`Failed`.
  - שגיאת 4xx מובילה מיד ל־`Failed`.
- בדיקות אינטגרציה ל־API עם `WebApplicationFactory`:
  - רצות על SQLite in-memory ‏(`EnsureCreated`), עם worker כבוי ו־fake provider.
  - הבדיקה מול SQL Server אמיתי היא הבדיקה הידנית מקצה לקצה.
  - Testcontainers נדחה בגלל מגבלת הזמן.
  - אם הזמן נגמר, זה הדבר הראשון שיורד.

## 13. מחוץ לתחום (מתועד ב־README)

- ממשק משתמש (אופציונלי במשימה).
- Idempotency key, דוחות מסירה, ספק שני, תור הודעות חיצוני, ניטור: מתוארים בחלק ב'.
- אימות משתמשים ו־Deploy.

## 14. סדר מימוש (הנחיות ל־Claude Code)

כל שלב מסתיים בקוד שמתקמפל ובבדיקות שעוברות, ואחריו commit נפרד.

1. **שלד:** solution, הפרויקטים ו־`docker-compose.yml` (כולל healthcheck ל־SQL Server עם `/opt/mssql-tools18/bin/sqlcmd -C` ו־`depends_on: service_healthy`).
2. **מודל ו־DB:** `Message`, `AppDbContext`, migration והפעלתה בעלייה.
3. **ולידציה:** `PhoneNumberNormalizer`, `SmsLengthCalculator` והבדיקות שלהם.
4. **API:** שלושת ה־endpoints, ProblemDetails ו־Swagger.
5. **שליחה:** `ISmsProvider`, `HttpSmsProvider`, `MessageSender` (עם בדיקות) ו־`SendingWorker`.
6. **ספק מדומה.**
7. **בדיקה ידנית מקצה לקצה** עם `docker compose up --build`: לשלוח, לראות שינוי סטטוס ולסנן.

כללים:
- קוד ושמות באנגלית.
- בלי ספריות מיותרות.
- כל הזמנים ב־UTC.
- הגדרות דרך `appsettings` ומשתני סביבה.
