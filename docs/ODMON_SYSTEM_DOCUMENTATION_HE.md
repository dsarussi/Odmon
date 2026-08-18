# תיעוד מערכת ODMON

> **עדכון אחרון:** 31 ביולי 2026  
> **קהל יעד:** מפתחים, DevOps, מפעילים, בעלי עניין

---

## תוכן עניינים

1. [סקירת מערכת](#1-סקירת-מערכת)
2. [ארכיטקטורה](#2-ארכיטקטורה)
3. [Workers ותחומי אחריות](#3-workers-ותחומי-אחריות)
4. [זרימות פיצ'רים](#4-זרימות-פיצרים)
   - 4.1 [סנכרון Odcanit → Monday](#41-סנכרון-odcanit--monday)
   - 4.2 [סנכרון דיון קרוב](#42-סנכרון-דיון-קרוב)
   - 4.3 [אישור הגעה לדיון → כתיבת נספח](#43-אישור-הגעה-לדיון--כתיבת-נספח)
   - 4.4 [Backfill אישורי הגעה](#44-backfill-אישורי-הגעה)
   - 4.5 [Backfill דיונים (ייבוא מרוכז)](#45-backfill-דיונים-ייבוא-מרוכז)
   - 4.6 [קליטת מסמכים (לוח שאלון)](#46-קליטת-מסמכים-לוח-שאלון)
   - 4.7 [קליטת מסמכים (לוח משימות)](#47-קליטת-מסמכים-לוח-משימות)
   - 4.8 [כתיבת נספח סיפור תאונה](#48-כתיבת-נספח-סיפור-תאונה)
   - 4.9 [סיכומי שיחות Voicenter → כתיבת נספח](#49-סיכומי-שיחות-voicenter--כתיבת-נספח)
   - 4.10 [התראות החלטות NetCourt](#410-התראות-החלטות-netcourt)
   - 4.11 [אוטומציית דוא"ל](#411-אוטומציית-דואל)
5. [אינטגרציות](#5-אינטגרציות)
   - 5.1 [Monday.com](#51-mondaycom)
   - 5.2 [Odcanit (ניהול תיקים משפטיים)](#52-odcanit-ניהול-תיקים-משפטיים)
   - 5.3 [Voicenter (מוקד טלפוני / סיכומי AI)](#53-voicenter-מוקד-טלפוני--סיכומי-ai)
6. [אחסון נתונים ולוגים](#6-אחסון-נתונים-ולוגים)
   - 6.1 [בסיס נתוני אינטגרציה](#61-בסיס-נתוני-אינטגרציה)
   - 6.2 [בסיס נתוני Odcanit](#62-בסיס-נתוני-odcanit)
   - 6.3 [מנגנוני מניעת כפילויות](#63-מנגנוני-מניעת-כפילויות)
   - 6.4 [לוגי כתיבה וביקורת](#64-לוגי-כתיבה-וביקורת)
7. [טיפול בשגיאות וחוסן](#7-טיפול-בשגיאות-וחוסן)
8. [דוא"ל סיכום יומי](#8-דואל-סיכום-יומי)
9. [התראות וניטור](#9-התראות-וניטור)
10. [קונפיגורציה ופריסה](#10-קונפיגורציה-ופריסה)
11. [ניהול סודות](#11-ניהול-סודות)
12. [מצבי בדיקה](#12-מצבי-בדיקה)

---

## 1. סקירת מערכת

**במילים פשוטות:**  
ODMON הוא גשר אוטומטי בין מערכת ניהול התיקים המשפטיים של המשרד (Odcanit) ללוחות ניהול פרויקטים (Monday.com). המערכת מסנכרנת נתוני תיקים, קולטת מסמכים מטפסים מקוונים, כותבת הערות הקשורות לבית המשפט בחזרה ל-Odcanit, ומושכת סיכומי שיחות טלפון שנוצרו על ידי AI ומצרפת אותם לתיקים.

**סיכום טכני:**  
ODMON הוא שירות Worker של .NET 8 הפועל כשירות Windows. הוא מארח מספר Workers ברקע שמתאמים זרימת נתונים בין Odcanit (שרת SQL Server מקומי לניהול תיקים משפטיים), Monday.com (לוחות ניהול פרויקטים SaaS), ו-Voicenter (טלפוניה ענן עם סיכום שיחות AI). המערכת משתמשת ב-Entity Framework Core לגישה לנתונים, סקירה תקופתית (polling) לסנכרון, HTTP/GraphQL עבור Monday ו-Voicenter, ו-Stored Procedures לכתיבה ל-Odcanit.

**יכולות ליבה:**

| יכולת | כיוון | תיאור |
|-------|-------|-------|
| סנכרון תיקים | Odcanit → Monday | שכפול נתוני תיקים כפריטי Monday |
| סנכרון דיונים | Odcanit → Monday | מעקב אחר הדיון הקרוב הבא לכל תיק |
| אישור הגעה לדיון | Monday → Odcanit | כתיבת אישור/דחיית הגעה כנספח |
| קליטת מסמכים | Monday → Odcanit | הורדת קבצים מ-Monday וייבוא ל-Odcanit |
| סיפור תאונה | Monday → Odcanit | הרכבת תשובות שאלון לטקסט נספח |
| סיכומי שיחות | Voicenter → Odcanit | צירוף סיכומי שיחות AI כנספחים לתיקים |
| החלטות NetCourt | Odcanit → דוא"ל | שליחת התראות לעובד מנותב, עם קובץ PDF במאמץ מיטבי |
| אוטומציית דוא"ל | Microsoft 365 → דוא"ל | ניתוב והעברת הודעות לפי תיק ולקוח; ללא תיוק ב-Odcanit |
| ניטור | פנימי | דוא"ל סיכום יומי, התראות קריטיות, מעקב כשלונות |

---

## 2. ארכיטקטורה

### מבנה רכיבים

```
┌──────────────────────────────────────────────────────┐
│                    ODMON Worker Service               │
│                                                      │
│  ┌──────────────┐  ┌─────────────────────────┐       │
│  │  SyncWorker   │  │ DocumentIngestionWorker │       │
│  └──────┬───────┘  └───────────┬─────────────┘       │
│         │                      │                      │
│         │   ┌──────────────────┤                      │
│         │   │  WorkerCoordinator (SemaphoreSlim)      │
│         │   │  (מונע עבודה מקבילית כבדה על ה-DB)     │
│         │   └──────────────────┘                      │
│         │                                             │
│  ┌──────┴──────────┐  ┌──────────────────────┐       │
│  │   SyncService    │  │DocumentIngestionSvc  │       │
│  │  (partial class) │  └──────────────────────┘       │
│  └─────────────────┘                                  │
│                                                      │
│  ┌──────────────────────┐  ┌─────────────────────┐   │
│  │VoicenterCallSummary  │  │ EmailBackground     │   │
│  │     Worker           │  │    Service           │   │
│  └──────────┬───────────┘  └──────────┬──────────┘   │
│             │                         │               │
│  ┌──────────┴───────────┐  ┌──────────┴──────────┐   │
│  │VoicenterCallSummary  │  │  EmailNotifier       │   │
│  │     Service          │  │  (תור SMTP)          │   │
│  └──────────────────────┘  └─────────────────────┘   │
│                                                      │
│  ┌──────────────────────┐  ┌─────────────────────┐   │
│  │HearingBackfillWorker │  │HearingApproval      │   │
│  │                      │  │  BackfillWorker      │   │
│  └──────────────────────┘  └─────────────────────┘   │
└──────────────────────────────────────────────────────┘
           │              │               │
     ┌─────┴─────┐  ┌────┴────┐   ┌──────┴──────┐
     │ Odcanit   │  │Monday   │   │ Voicenter   │
     │ SQL Server│  │.com API │   │ API         │
     └───────────┘  └─────────┘   └─────────────┘
```

### תיאום Workers

**במילים פשוטות:**  
רמזור תנועה מבטיח שרק Worker כבד אחד רץ בכל רגע נתון, כדי שבסיס הנתונים לא יהיה עמוס מדי.

**פירוט טכני:**  
`WorkerCoordinator` הוא Singleton שעוטף `SemaphoreSlim(1,1)`. ה-`SyncWorker`, ה-`DocumentIngestionWorker` וה-`HearingNearestScheduleWorker` מקבלים lease לפני עבודה כבדה על בסיסי הנתונים. אם ה-lease תפוס, המחזור המתוזמן מדולג בצורה מסודרת. ה-`EmailBackgroundService`, ה-`VoicenterCallSummaryWorker`, ה-`NetCourtDecisionAlertWorker` וה-`EmailAutomationWorker` אינם כפופים למתאם זה.

### הזרקת תלויות (Dependency Injection)

כל ה-Workers, שירותים ותשתיות רשומים ב-`Program.cs`. רישומים עיקריים:
- **Singletons:** `WorkerCoordinator`, `VoicenterApiClient`, `EmailNotifier`
- **Scoped (למחזור):** `SyncService`, `VoicenterCallSummaryService`, `DocumentIngestionService`, `HearingApprovalSyncService`, `HearingNearestSyncService`, `NispahWriterService`
- **Hosted services:** `SyncWorker`, `HearingNearestScheduleWorker`, `DocumentIngestionWorker`, `EmailBackgroundService`, `VoicenterCallSummaryWorker`, `NetCourtDecisionAlertWorker`, `EmailAutomationWorker`, `HearingBackfillWorker`, `HearingApprovalBackfillWorker`
- **שירותי NetCourt:** `NetCourtDecisionAlertService`, `SqlNetCourtDocumentReader`, `SqlNetCourtDocumentFileResolver`
- **שירותי אוטומציית דוא"ל:** `EmailAutomationService`, `MicrosoftGraphEmailAutomationClient`, `EmailAutomationCaseResolver`
- **DbContexts:** `IntegrationDbContext`, `OdcanitDbContext` (שניהם Scoped, SQL Server)

---

## 3. Workers ותחומי אחריות

### SyncWorker

**מה הוא עושה:** קורא נתוני תיקים מ-Odcanit באופן תקופתי ויוצר או מעדכן פריטים מתאימים ב-Monday.com.

| מאפיין | ערך |
|--------|-----|
| מחלקה | `SyncWorker` |
| תדירות | מוגדרת דרך `Sync:IntervalSeconds` (ברירת מחדל 180 שניות) |
| תיאום | רוכש חכירה מ-`WorkerCoordinator` |
| מאציל ל | `SyncService` |
| קונפיגורציה | `Sync`, `Monday`, `OdcanitLoad`, `Testing`, `Safety` |
| התנהגות מרכזית | אבחון עליה, לוג heartbeat, התראות כשל SQL |

### DocumentIngestionWorker

**מה הוא עושה:** מוריד קבצים מלוחות שאלון ומשימות ב-Monday ומייבא אותם למערכת המסמכים של Odcanit.

| מאפיין | ערך |
|--------|-----|
| מחלקה | `DocumentIngestionWorker` |
| תדירות | מוגדרת דרך `MondayDocumentIngestion:IntervalSeconds` (ברירת מחדל 300 שניות) |
| תיאום | רוכש חכירה מ-`WorkerCoordinator` |
| מאציל ל | `DocumentIngestionService` |
| קונפיגורציה | `MondayDocumentIngestion`, `OdcanitDocuments` |
| התנהגות מרכזית | בודק תקינות טבלת `NispahDeduplications` בעלייה |

### EmailBackgroundService

**מה הוא עושה:** מעבד תור דוא"ל (התראות, תקצירים) ושולח סיכום HTML יומי של פעילות המערכת.

| מאפיין | ערך |
|--------|-----|
| מחלקה | `EmailBackgroundService` |
| תדירות | עיבוד תור רציף + משימות תקופתיות |
| תיאום | ללא (לא מוגבל לעולם) |
| קונפיגורציה | `Email` |
| התנהגות מרכזית | סיכום יומי בשעה ישראלית מוגדרת, תקציר תקופתי, SMTP דרך Office365 |

### VoicenterCallSummaryWorker

**מה הוא עושה:** שואב רשומות שיחות מ-Voicenter, מושך סיכומי AI, וכותב אותם כנספחים לתיקי Odcanit תואמים.

| מאפיין | ערך |
|--------|-----|
| מחלקה | `VoicenterCallSummaryWorker` |
| תדירות | מוגדרת דרך `VoicenterCallSummaries:IntervalHours` (ברירת מחדל 12 שעות) |
| תיאום | ללא (בעיקר קריאות API חיצוניות) |
| מאציל ל | `VoicenterCallSummaryService` |
| קונפיגורציה | `VoicenterCallSummaries` |
| התנהגות מרכזית | בידוד לכל שיחה, זיהוי Worker תקוע, התראות חריגה, מיתון התראות |

### HearingBackfillWorker

**מה הוא עושה:** ייבוא מרוכז חד-פעמי של נתוני דיונים מטבלת staging ב-SQL לפריטי Monday.

| מאפיין | ערך |
|--------|-----|
| מחלקה | `HearingBackfillWorker` |
| התנהגות | מריץ אצוות עד שטבלת המקור ריקה, ואז מסתיים |
| קונפיגורציה | `HearingBackfill` |
| שימוש טיפוסי | מושבת בייצור; מופעל עבור אירועי מיגרציה |

### HearingApprovalBackfillWorker

**מה הוא עושה:** Backfill חד-פעמי היסטורי שכותב נספחי אישור הגעה לדיון עבור כל הפריטים שאושרו או נדחו בעבר.

| מאפיין | ערך |
|--------|-----|
| מחלקה | `HearingApprovalBackfillWorker` |
| התנהגות | ריצה אחת ואז מסתיים |
| קונפיגורציה | `HearingApprovalBackfill` |
| שימוש טיפוסי | מושבת; מופעל לאחר השקת הפיצ'ר לכיסוי נתונים היסטוריים |

### HearingNearestScheduleWorker

**מה הוא עושה:** מריץ את אותו reconciliation של הדיון הקרוב באופן עצמאי מהסנכרון הראשי, בשעות ישראל קבועות.

| מאפיין | ערך |
|--------|-----|
| מחלקה | `HearingNearestScheduleWorker` |
| לוח זמנים | `HearingNearestSchedule:TimesIsrael`; ברירת המחדל וההגדרה הנוכחית הן `07:00`, `11:00`, `15:00`, `19:00`, `23:00` לפי שעון ישראל |
| תיאום | מקבל lease מ-`WorkerCoordinator` |
| מאציל ל | `HearingNearestSyncService` |
| מצב ברירת מחדל | מושבת (`HearingNearestSchedule:Enabled=false`) |

### NetCourtDecisionAlertWorker

**מה הוא עושה:** סורק החלטות NetCourt זכאיות ב-Odcanit ושולח התראות דוא"ל מנותבות, עם PDF מאומת כאשר ניתן לצרפו.

| מאפיין | ערך |
|--------|-----|
| תדירות | `NetCourtDecisionAlerts:IntervalSeconds` (מינימום 30 שניות; מוגדר 300) |
| מאציל ל | `NetCourtDecisionAlertService` |
| מצב עמיד | זהויות מסמך בטבלת `NetCourtDecisionAlerts` |

### EmailAutomationWorker

**מה הוא עושה:** מנטר תיבות Microsoft 365 מוגדרות ומעביר הודעות תואמות. הוא אינו מתייק הודעות או קבצים מצורפים ב-Odcanit.

| מאפיין | ערך |
|--------|-----|
| תדירות | `EmailAutomation:IntervalMinutes` (מינימום דקה; מוגדר 3) |
| מאציל ל | `EmailAutomationService` |
| מצב ברירת מחדל | מושבת; `DryRun=true`, `RealForwardEnabled=false` |
| מצב עמיד | `EmailAutomationMailboxStates`, `EmailAutomationLogs` |

---

## 4. זרימות פיצ'רים

### 4.1 סנכרון Odcanit → Monday

**במילים פשוטות:**  
מידע על תיקים ממערכת המשרד מופיע אוטומטית בלוחות Monday.com, ושומר על כולם מעודכנים ללא הזנת נתונים ידנית.

**זרימה טכנית:**

1. **בחירת תיקים** — `SyncService` קובע אילו תיקים לטעון:
   - *מצב Allowlist* (`OdcanitLoad:EnableAllowList`): טוען רק TikCounters/TikNumbers מוגדרים
   - *מצב Listener* (`Sync:ListenerUpdateOnly=true` יחד עם `ListenerCreationCutoffDate`): מבצע bootstrap לתיקים לא ממופים שנוצרו מה-cutoff ואילך, ולאחר מכן reconciliation לכל האוכלוסייה הזכאית והממופה מאותו cutoff. מסלול הייצור הזה אינו משתמש ב-`vwExportToOuterSystems_ActionLog`
   - *מצב Legacy/non-listener*: שואל את `vwExportToOuterSystems_ActionLog` לתיקים שהשתנו מאז ה-watermark האחרון
2. **טעינת תיקים** — `IOdcanitReader` (`SqlOdcanitReader`) טוען נתוני תיק מלאים מ-Views של Odcanit, ומעשיר כל `OdcanitCase` עם לקוחות, צדדים, אירועי יומן, נתוני משתמש ונתוני חוזלפ
3. **קליטת תיקים חדשים (Bootstrap)** — תיקים חדשים (ללא `MondayItemMapping` קיים) עוברים תקופת המתנה (`Onboarding:CoolingPeriodDays`) לפני יצירתם כפריטי Monday. מנגנון זה מונע יצירת פריטים לתיקים שנסגרים או מתוקנים מיד
4. **זהות יציבה ו-Reconciliation** — `TikCounter` הוא המזהה הפנימי הקבוע והמפתח הייחודי במיפוי. `TikNumber` הוא מספר התיק המוצג והוא עשוי להשתנות. החיפוש מתחיל ב-`TikCounter + BoardId`, מסוגל ליישב מיפויים ישנים, ומשתמש בעמודת מספר התיק ב-Monday כמסלול שחזור. שינוי `TikNumber` מעדכן את אותו פריט Monday ואינו יוצר תיק חדש. checksums של תוכן ודיון יחד עם גרסת Odcanit מונעים קריאות API מיותרות
5. **סנכרון דיונים** — מואצל ל-`HearingNearestSyncService` (ראו 4.2)
6. **סנכרון אישור הגעה** — מואצל ל-`HearingApprovalSyncService` (ראו 4.3)
7. **Circuit Breaker** — אם כשלונות חורגים מ-`Monday:CircuitBreakerFailureThreshold`, הריצה מבוטלת ומטריקה נרשמת
8. **נעילת ריצה** — טבלת `SyncRunLocks` מונעת ריצות סנכרון חופפות (lease בשורה אחת עם תפוגה)
9. **מטריקות** — כל ריצה רושמת שורת `SyncRunMetric`: נוצרו, עודכנו, נכשלו, דולגו, משך, סטטוס circuit breaker

**טבלאות מרכזיות:** `MondayItemMappings`, `SyncLogs`, `SyncFailures`, `SyncRunMetrics`, `SyncRunLocks`, `ListenerStates`

### 4.2 סנכרון דיון קרוב

**במילים פשוטות:**  
לכל תיק, המערכת מוצאת את הדיון הקרוב הבא ומציגה את פרטיו (תאריך, שופט, עיר) בלוח Monday.

**זרימה טכנית:**

1. `HearingNearestSyncService.SyncNearestHearingsAsync` נקרא בכל מחזור סנכרון
2. אותו שירות יכול לרוץ גם מ-`HearingNearestScheduleWorker`, שמושבת כברירת מחדל, בשעות ישראל קבועות (`07:00`, `11:00`, `15:00`, `19:00`, `23:00`)
3. `HearingSelector.PickNearestUpcomingHearing` בוחן שורות יומן עתידיות ומעדיף את הדיון הפעיל הקרוב ביותר; רק אם אין דיון פעיל הוא עובר לדיון מבוטל, ולאחר מכן לדיון שהועבר
4. שינויים מזוהים בהשוואה ל-`HearingNearestSnapshots`
5. סטטוס הדיון מתעדכן באופן עצמאי (`0 → "פעיל"`, `1 → "מבוטל"`, `2 → "הועבר"`), בתנאי שהתווית מותרת בעמודת Monday
6. שינוי שם שופט עצמאי. תאריך ושעת הדיון נכתבים רק כאשר קיימים גם שופט וגם עיר בית משפט אפקטיבית. העיר האפקטיבית היא `City` משורת היומן, ובחסרונו `CourtName`
7. שירות הדיונים העצמאי אינו כותב במכוון ל-`Monday:CourtCityColumnId`; עמודה זו נשארת בבעלות הסנכרון הראשי ומקבלת רק את `OdcanitCase.LegalCourtName` מ-UserData המשפטי
8. חסימת תאריך/שעה מונעת מאוטומציות התראה ב-Monday לפעול עם הקשר חלקי של שופט או בית משפט
9. `OdcanitWrites:DryRun=true` רושם את הצעדים המתוכננים אך אינו מעדכן את Monday ואינו מקדם snapshot

**טבלאות מרכזיות:** `HearingNearestSnapshots`, `MondayItemMappings`

### 4.3 אישור הגעה לדיון → כתיבת נספח

**במילים פשוטות:**  
כאשר לקוח מאשר או מסרב להגיע לדיון דרך לוח Monday, המערכת רושמת אוטומטית את ההחלטה בתיק Odcanit.

**זרימה טכנית:**

1. `HearingApprovalSyncService.SyncAsync` רץ בכל מחזור סנכרון
2. קורא עמודת Monday `color_mkzbmv1b` ("אישור הגעה לדיון") עבור כל הפריטים הממופים
3. משווה ל-`MondayHearingApprovalStates` לזיהוי מעברים
4. רק מעברים בני-פעולה (→ אישור אינדקס `1`, → דחייה אינדקס `2`) מפעילים כתיבה
5. קורא ל-`IOdcanitWriter.AppendNispahAsync` עם Stored Procedure `dbo.Klita_Interface_NispahDetails`
6. טקסט נספח: "אישר הגעה לדיון" (אושר) או "לא אישר הגעה לדיון" (נדחה)
7. כותב רשומת `NispahWriteLog` (`SourceKind="HearingApproval"`) לביקורת
8. מעדכן `MondayHearingApprovalState` למניעת כתיבות כפולות
9. **מצב DryRun**: כאשר `OdcanitWrites:DryRun` מופעל — רושם ללוג בלבד, ללא שינויי מצב וללא כתיבה ל-Odcanit

**טבלאות מרכזיות:** `MondayHearingApprovalStates`, `NispahWriteLogs`

### 4.4 Backfill אישורי הגעה

**במילים פשוטות:**  
ריצה חד-פעמית שעוברת על כל האישורים/דחיות ההיסטוריים וכותבת אותם ל-Odcanit, עבור תיקים שאושרו לפני הפעלת הפיצ'ר.

**זרימה טכנית:**

1. `HearingApprovalBackfillService.RunAsync` עובר על כל ה-`MondayItemMappings`
2. לכל מיפוי, שואב ערך עמודת אישור הגעה מ-Monday
3. מדלג אם קיימת רשומת `NispahWriteLog` עם `SourceKind="HearingApproval"` עבור TikCounter זה (הוכחה לכתיבה קודמת, לא רק מעקב מצב)
4. דוחה מיפויים עם `TikCounter <= 0`; כללי תקינות המיפוי הנוכחיים אוסרים TikCounters סינתטיים או שליליים
5. כותב נספח דרך `IOdcanitWriter.AppendNispahAsync` ורושם `NispahWriteLog`
6. תומך ב-`DryRun`, `MaxItems`, מסנני `OnlyTikCounters`, ו-`ThrottleMs` להאטה

**טבלאות מרכזיות:** `MondayItemMappings`, `NispahWriteLogs`, `MondayHearingApprovalStates`

### 4.5 Backfill דיונים (ייבוא מרוכז)

**במילים פשוטות:**  
מייבא אצווה של רשומות דיונים מטבלת staging לפריטי Monday.com, בשימוש במהלך אירועי מיגרציית נתונים.

**זרימה טכנית:**

1. `HearingBackfillService.RunAsync` קורא שורות מטבלת מקור מוגדרת (לדוגמה `dbo.HearingBackfill_JunJul2026`)
2. מטפל בחוסר התאמת טיפוסים: `ClientNumber` כ-`string` (מ-`NVARCHAR`), `HearingTime` כ-`string` (מ-`TimeSpan`)
3. בונה ערכי עמודות Monday ויוצר פריטים דרך Monday API
4. מגדיר את עמודת הסטטוס המוגדרת על כל פריט חדש שנוצר ב-Monday
5. יוצר רשומות `MondayItemMapping` עבור פריטים חדשים

**טבלאות מרכזיות:** טבלת מקור מוגדרת (עמודות בעברית), `MondayItemMappings`

**הרצת יוני-יולי 2026:** יש להשתמש בטבלת staging אחת בשם `dbo.HearingBackfill_JunJul2026`. הבחירה בטבלה נעשית דרך קונפיגורציה, ללא שינוי קוד, כל עוד צורת העמודות זהה:

```sql
CREATE TABLE dbo.HearingBackfill_JunJul2026
(
    [תאריך דיון] date NULL,
    [שעת דיון] time(0) NULL,
    [שם שופט] nvarchar(256) NULL,
    [עיר בית משפט] nvarchar(256) NULL,
    [טלפון נהג] nvarchar(64) NULL,
    [שם נהג] nvarchar(256) NULL,
    [מספר תיק] nvarchar(64) NULL,
    [מספר לקוח] nvarchar(64) NULL,
    [תאריך אירוע] date NULL
);
```

קונפיגורציית הרצה:

```json
"HearingBackfill": {
  "Enable": true,
  "SourceTable": "dbo.HearingBackfill_JunJul2026",
  "BoardId": 5035534500,
  "StatusColumnId": "color_mm12y7zr",
  "ImportedStatusIndex": 1,
  "BatchSize": 10
}
```

אזהרת תפעול: ל-`HearingBackfill` אין מצב dry-run. יש להפעיל אותו רק בזמן חלון הייבוא, לעקוב בלוגים אחרי `SourceTable`, `BoardId`, `BatchSize`, `Processed`, `Created`, `Skipped`, ו-`Failed`, ולהחזיר מיד בסיום את `HearingBackfill:Enable=false`.

### 4.6 קליטת מסמכים (לוח שאלון)

**במילים פשוטות:**  
קבצים שהועלו לטופס שאלון Monday (תמונות, PDF, וידאו) מיובאים אוטומטית לתיק Odcanit, כך שלעורכי הדין יש את כל המסמכים במקום אחד.

**זרימה טכנית:**

1. `DocumentIngestionService.RunIngestionAsync` שואב פריטים מלוח השאלון ב-Monday
2. לכל פריט, מוצא את התיק המקושר דרך עמודת relation → חיפוש TikNumber
3. לכל עמודת קבצים מוגדרת (`file_mm0qwtat`, `file_mkzr2cmr`, `file_mkyet713`), מוריד assets מ-Monday
4. **צינור אימות** לכל asset:
   - בדיקת Denylist: סיומות מסוכנות (`exe`, `msi`, `bat` וכו') נחסמות → `DENYLIST_EXTENSION`
   - בדיקת גודל: מגבלה לכל עמודה (לדוגמה `file_mkyet713` = 35 MiB) ומגבלה גלובלית (50 MiB) → `FILE_TOO_LARGE`
   - אימות סיומת: זיהוי MIME + magic bytes עבור קבצים עמומים
   - הבחנה HEIC/HEIF מול MP4/MOV: על פי major brand ב-ISO BMFF `ftyp`
5. הקובץ נשמר לתיקיית inbox, ואז Stored Procedure `dbo.ProcDocuments_AddNewDocument` ב-Odcanit יוצר רשומת מסמך
6. הקובץ מועבר לנתיב המסמך הסופי ב-Odcanit
7. מעקב ב-`MondayDocumentImports` עם מצבים: `Pending → Downloaded → SpCreated → Copied → Verified → Success` או `Failed`
8. רשומות כשל עוברות ניסיון חוזר עד `MaxRetryCount`; כשלי אימות קבועים מועברים ישירות לגבול הניסיונות

**סיומות קבצים נתמכות:** `pdf`, `jpg`, `jpeg`, `png`, `docx`, `doc`, `mp4`, `mov`, `qt`, `heic`, `heif`

**סיומות קבצים חסומות:** `exe`, `msi`, `bat`, `cmd`, `ps1`, `js`, `vbs`, `scr`, `com`, `hta`, `jar`, `zip`, `rar`, `7z`

**טבלאות מרכזיות:** `MondayDocumentImports`

### 4.7 קליטת מסמכים (לוח משימות)

**במילים פשוטות:**  
מסמכי Word שהועלו ללוח "משימות" נפרד ב-Monday מיובאים גם הם אוטומטית ל-Odcanit.

**זרימה טכנית:**

1. אותו `DocumentIngestionService.RunIngestionAsync` מטפל בכך כאשר `TasksSource:Enabled` מופעל
2. שואב פריטים מלוח שונה (`TasksSource:BoardId`)
3. פותר TikNumber דרך עמודת lookup
4. מוריד קובץ מעמודת הקובץ בלוח המשימות
5. מעבד רק את קובץ ה-`.doc`/`.docx` הראשון ורק כאשר סטטוס המשימה הוא אחת מתוויות המוכנות המוגדרות. פריט מוכן ללא קובץ הופך לכשל timeout מתועד לאחר `WaitForFileTimeoutHours`

`TasksSource:TestTikNumber` מצמצם את מקור הייצור; הוא אינו מצב dry-run. לצינור קליטת המסמכים אין מתג dry-run כללי.

**קונפיגורציה:** מקוננת תחת `MondayDocumentIngestion:TasksSource`

### 4.8 כתיבת נספח סיפור תאונה

**במילים פשוטות:**  
תשובות מהשאלון המקוון על התאונה (מה קרה, האם היו עדים וכו') מורכבות לסיכום ונכתבות כהערה בתיק Odcanit.

**זרימה טכנית:**

1. במהלך קליטת מסמכים, `ProcessAccidentStoryAsync` רץ לכל פריט שאלון
2. `AccidentStoryComposer.Compose` אוסף ערכים מעמודות Monday מוגדרות (טקסט ארוך, שדות סטטוס)
3. מרכיב בלוק טקסט מעוצב בעברית עם זוגות שאלה-תשובה
4. כותב דרך `NispahWriterService.CreateNispahAsync` (SP `dbo.Klita_Interface_NispahDetails`)
5. אידמפוטנטיות: דגל `CaseAnnexWriteState.AccidentStoryAnnexWritten` לכל TikCounter מונע כתיבות כפולות
6. מניעת כפילויות נוספת דרך טבלת `NispahDeduplications` (hash של תוכן)

**טבלאות מרכזיות:** `CaseAnnexWriteStates`, `NispahDeduplications`, `NispahAuditLogs`

### 4.9 סיכומי שיחות Voicenter → כתיבת נספח

**במילים פשוטות:**  
כאשר מתבצעת שיחת טלפון ללקוח או עד וה-AI יוצר סיכום השיחה, הסיכום מצורף אוטומטית לתיק Odcanit הרלוונטי.

**זרימה טכנית:**

1. `VoicenterCallSummaryWorker` רץ במחזור של 12 שעות (מוגדר)
2. שואב CDR (רשומות פרטי שיחה) מ-API של Voicenter `/hub/cdr/` עם אימות קוד בגוף הבקשה
3. מסנן: רק שיחות שנענו (מוגדר), משך מינימלי, חלון lookback
4. לכל רשומת CDR מתאימה, שואב פרטי שיחה מלאים מ-`/Call/History/{CallID}` עם Bearer token
5. מחלץ סיכום AI מ-`Data.ai_data.insights.summary`
6. פותר את מספר הטלפון מ-`Data.ai_data.client_phone` → מספר יעד → מספר מתקשר
7. מנרמל לפורמט טלפון ישראלי (דטרמיניסטי `0XX-XXXXXXX`)
8. מתאים ישירות לתיקי Odcanit דרך `vwExportToOuterSystems_UserData`, לפי `PhoneFieldNames` מוגדרים או רשימת ברירת המחדל של טלפוני עד, נהג, בעל פוליסה, צד ג', עו"ד צד ג' ותובע
9. **כלל התאמה מרובה**: כותב לכל התיקים המתאימים (לא רק הראשון)
10. מניעת כפילויות לפי `CallID + TikCounter` דרך `NispahWriteLogs` (`SourceKind="VoicenterCall"`, `SourceItemId` = hash של CallID)
11. כותב נספח דרך `IOdcanitWriter.AppendNispahAsync`
12. פורמט נספח: תאריך, שעה, משך, סטטוס (בעברית), וטקסט סיכום AI

**בידוד לכל שיחה:** כל CallID מעובד ב-try/catch משלו. payload פגום של שיחה אחת לא מכשיל את כל המחזור.

**זיהוי Worker תקוע:** אם לא התרחש מחזור מוצלח תוך `StaleWorkerThresholdHours` (ברירת מחדל 18 שעות), נשלח דוא"ל התראה.

**מצב בדיקה:** כאשר `TestMode=true` ו-`TestCallId` מוגדר, רק שיחה אחת מעובדת עם לוגים אבחוניים. מצב זה פונה ל-detail endpoint ועלול לכתוב נספח חי ב-Odcanit; רק `VoicenterBackfill:DryRun=true` מונע כתיבה.

**מעקב מכסה שבועית (מאי 2026):** Voicenter אוכפת מגבלת שימוש שבועית (כיום 400 לשבוע עבור משתמש 203570) על נקודת הקצה `Call/History/{CallID}`. ODMON רושם כעת כל קריאת API ב-`VoicenterApiRequestLogs` בהפרדה לפי `EndpointType` (`CdrList` מול `CallHistoryDetail`), סופר את שבוע ה-ISO הנוכחי, ושולח דוא"ל אזהרה פעם בשבוע כאשר השימוש מגיע ל-`WeeklyUsageWarningThreshold` (ברירת מחדל 350). כאשר Voicenter מחזירה HTTP 401 או גוף תגובה המכיל "weekly usage limit" / "usage limit" / "quota" / "limit reached", `VoicenterApiClient` זורקת `VoicenterQuotaExceededException`; השירות מפסיק לשלוח קריאות פרטים נוספות במחזור וסופר את שורות ה-CDR הנותרות כ-`SkippedDueToQuotaExceeded`.

**מטמון מצב עיבוד מקומי:** `VoicenterCallProcessingStates` שומר מצב לכל CallID (`Written`, `NoAI`, `NoMatch`, `Duplicate`, `Failed`, `QuotaExceeded`). המצבים `Written` ו-`Duplicate` תמיד מדולגים. רשומות `QuotaExceeded` עדכניות, וגם `NoMatch` כאשר הוגדר לכך חלון, עשויות לעבור ניסיון חוזר בתוך חלון ה-lookback ועד `MaxDeferredReprocessCallsPerRun`. `NispahWriteLogs` נבדקת לפני שליפת הפרטים ושוב לכל תיק תואם.

**מצב Backfill ידני:** בלוק קונפיגורציה `VoicenterBackfill` מאפשר טווח תאריכים רחב יותר חד-פעמי (לדוגמה: שחזור כל השיחות מאז 27.04.2026 לאחר הפסקת מכסה). ברירת המחדל `DryRun=true` לתצוגה מקדימה בטוחה. ראו `docs/VOICENTER_QUOTA_RUNBOOK.md`.

**טבלאות מרכזיות:** `NispahWriteLogs`, `VoicenterApiRequestLogs`, `VoicenterQuotaWarningStates`, `VoicenterCallProcessingStates`

### 4.10 התראות החלטות NetCourt

`NetCourtDecisionAlertWorker` קורא מ-`vwNetCourtDocs` שורות שבהן `DocType IN (2,3)` ו-`DocDate >= NetCourtDecisionAlerts:StartFromDocDate`. סדר העדיפות לזהות עמידה הוא `CourtDocumentID`, אחריו `ODDocID`, `DecisionID`, ולבסוף `Counter` של המקור. זהויות שכבר תועדו מוסרות לפני החלת `MaxBatchSize`.

התיק נפתר ב-Odcanit והלקוח מנותב לפי `ClientNumberToRecipientEmail`. במצב Test ההודעה הראשית נשלחת רק ל-`TestRecipient`; במצב Live היא נשלחת לעובד המנותב. `BccRecipients` חל רק על הודעות NetCourt. כאשר אין ניתוב ו-fallback מושבת, נרשם `MissingRouting` ולא נשלח דוא"ל.

כאשר צירוף PDF מופעל, הנתיב מתקבל בצורה positional דרך `dbo.procDocumentsGroup_BuildDocPath` לפי `ODDocID` וסיומת `.pdf`. הנתיב חייב להיות תחת `AttachmentAllowedRoots`, להתקיים, להסתיים ב-`.pdf`, להתחיל ב-`%PDF` ולא לחרוג מ-`MaxAttachmentBytes`. הצירוף הוא best effort: כשל בצירוף אינו מונע את שליחת ההתראה ללא הקובץ. ראו [NETCOURT_DECISION_ALERTS.md](NETCOURT_DECISION_ALERTS.md).

### 4.11 אוטומציית דוא"ל

`EmailAutomationWorker` משתמש בהרשאות אפליקציה של Microsoft Graph ובשאילתות delta מסוג `changeType=created` לכל תיבה ותיקייה מוגדרות. תיבה חדשה מתחילה מ-`StartProcessingFromUtc` או מזמן האתחול, ולכן דואר היסטורי אינו מעובד כברירת מחדל. מצב delta מתקדם רק לאחר השלמת כל רצף הדפים; throttling או הגעה למגבלת המחזור משאירים אותו ללא שינוי לניסיון הבא.

הכללים מזהים מספר הליך משפטי בנושא, פותרים אותו דרך Hozlap/UserData לתיק Odcanit, מפענחים את מספר הלקוח ומנתבים לאמיר (`5,8,23,253,101,3`), ליונתן (`2,15`) או לעדן (יתר הלקוחות). Dry-run רושם `DryRunWouldForward`. העברת בדיקה מוגבלת ל-`odmon@ezer-law.com`. העברה אמיתית דורשת `RealForwardEnabled=true` ו-`DryRun=false`, ומיישמת הגנות מפני לולאה: בעל התיבה, נמען שכבר נמצא ב-To/Cc, נושא reply/forward, שולח היעד ודואר שנוצר על ידי האוטומציה.

הודעות שאין בהן מספר תיק, שאינן נפתרות לתיק, או שנפתרות באופן דו-משמעי אינן נשמרות ב-ODMON, ופרטים מזהים שלהן אינם נכתבים ללוגים. עבור הודעה שהותאמה נשמרים ב-`EmailAutomationLogs` רק התיבה התפעולית, טביעות HMAC ממופתחות, `TikCounter` שנפתר, עובד היעד, פעולה/תוצאה, קטגוריית שגיאה מנוטרלת וחותמות זמן. הנושא, השולח, הנמענים, מזהי Graph/Internet גולמיים, מספר ההליך שזוהה, גוף ההודעה, מידע על קבצים מצורפים ו-`TikNumber` אינם נשמרים.

לפני פעולת Graph נשמר מפתח idempotency ייעודי מסוג HMAC-SHA-256. הסוד היציב מסופק דרך `EmailAutomation:FingerprintKey`; טביעת ההעברה מחושבת ממזהה ה-Internet של ההודעה (או מזהה Graph כגיבוי), שם הכלל ועובד היעד. טביעת הודעה נפרדת מחושבת ממזהה המקור ומהתיבה. אף טביעה אינה כוללת נושא, שולח, גוף, נמענים או פרטי קבצים מצורפים. ההודעה המקורית, לרבות הקבצים המצורפים, מועברת בתוך Microsoft 365 בלי הורדה או שמירה על ידי ODMON. ODMON אינו מוחק, מעביר תיקייה, מאחסן בארכיון או משנה את הודעת המקור, אינו יוצר רשומות מסמך ב-Odcanit ואינו מתייק קבצים מצורפים נכנסים.

---

## 5. אינטגרציות

### 5.1 Monday.com

**במילים פשוטות:**  
Monday.com הוא לוח הפרויקטים שבו המשרד עוקב אחרי תיקים. ODMON קורא ממנו וכותב אליו.

| רכיב | מטרה |
|------|------|
| `IMondayClient` / `MondayClient` | מוטציות GraphQL (יצירת פריט, שינוי ערכי עמודות) |
| `IMondayMetadataProvider` / `MondayMetadataProvider` | מטא-דאטה של לוח, הגדרות עמודות, פתרון תוויות מורשות עם מטמון |
| `DocumentIngestionMondayService` | שאילתות GraphQL עבור פריטי שאלון/משימות, כתובות הורדת assets, ערכי עמודות |

**אימות:** API token נפתר דרך `ISecretProvider` (מפתח: `Monday__ApiToken`).

**מבנה לוחות:**
- **לוח תיקים** (`Monday:BoardId` / `CasesBoardId`): מעקב תיקים ראשי, 90+ עמודות ממופות מ-Odcanit
- **לוח שאלון** (`MondayDocumentIngestion:BoardId`): טפסי קליטת לקוח עם העלאות קבצים
- **לוח משימות** (`MondayDocumentIngestion:TasksSource:BoardId`): מסמכי משימות של עורכי דין

**מיפוי עמודות:** מוגדר בהגדרות `Monday:*ColumnId`. מיפוי מלא מתועד ב-`docs/ODMON_Monday_Mapping.md`.

### 5.2 Odcanit (ניהול תיקים משפטיים)

**במילים פשוטות:**  
Odcanit הוא בסיס הנתונים הראשי של המשרד. ODMON קורא נתוני תיקים ממנו וכותב בחזרה מסמכים והערות.

**גישת קריאה (Views):**

| View | Entity | שימוש |
|------|--------|-------|
| `vwExportToOuterSystems_Files` | `OdcanitCase` | נתוני תיק ראשיים |
| `vwExportToOuterSystems_LoginUsers` | `OdcanitUser` | חיפוש משתמשים |
| `vwExportToOuterSystems_Clients` | `OdcanitClient` | פרטי לקוחות |
| `vwExportToOuterSystems_vwSides` | `OdcanitSide` | צדדים בתיק |
| `vwExportToOuterSystems_YomanData` | `OdcanitDiaryEvent` | יומן בית משפט / דיונים |
| `vwExportToOuterSystems_UserData` | `OdcanitUserData` | ערכי שדות מותאמים |
| `vwHozlapFormsData_TikMainData` | `OdcanitHozlapMainData` | מספרי תיק בבית משפט |
| `vwExportToOuterSystems_ActionLog` | (SQL גולמי) | זיהוי שינויים (Change Feed) |

**גישת כתיבה (Stored Procedures):**

| Stored Procedure | משמש את | מטרה |
|-----------------|---------|------|
| `dbo.Klita_Interface_NispahDetails` | `SqlOdcanitWriter`, `NispahWriterService` | כתיבת נספח/הערה לתיק |
| `dbo.ProcDocuments_AddNewDocument` | `OdcanitDocumentWriter` | יצירת רשומת מסמך (ייבוא קובץ) |

**חיבור:** SQL Server, מחרוזת חיבור נפתרת דרך `ISecretProvider` (מפתח: `OdcanitDb__ConnectionString`).

### 5.3 Voicenter (מוקד טלפוני / סיכומי AI)

**במילים פשוטות:**  
Voicenter היא מערכת הטלפון. היא מקליטה שיחות ויוצרת סיכומי AI. ODMON מושך את הסיכומים ומצרף אותם לתיקים.

**נקודות API:**

| נקודת קצה | אימות | מטרה |
|-----------|-------|------|
| `POST https://api.voicenter.com/hub/cdr/` | פרמטר `code` בגוף הבקשה | שליפת רשימת CDR |
| `GET https://api-manager.voicenter.co/api-manager-v1/Call/History/{callId}` | כותרת Bearer token | שליפת פרטי שיחה + סיכום AI |

**מבנה תשובת CDR:** אובייקט שורש עם מאפיין `CDR_LIST` המכיל מערך של רשומות שיחות.

**נתיבי מפתח בפרטי שיחה:**
- סיכום AI: `Data.ai_data.insights.summary`
- טלפון לקוח: `Data.ai_data.client_phone` (ראשי), `Data.cdr_data.client_phone` (fallback)
- סטטוס: `Data.cdr_data.DialStatus`
- משך: `Data.cdr_data.Duration`

**ניתוח JSON מגן:** כל גישה ל-`JsonElement` מוגנת בבדיקות `ValueKind`. payloads פגומים מדולגים עם אזהרה, ולעולם לא גורמים לקריסת ה-Worker.

---

## 6. אחסון נתונים ולוגים

### 6.1 בסיס נתוני אינטגרציה

**במילים פשוטות:**  
בסיס נתוני האינטגרציה הוא "הזיכרון העובד" של ODMON — הוא עוקב אחר מה סונכרן, אילו מסמכים מעובדים, ומה נכתב ל-Odcanit.

**סקירת ישויות:**

| טבלה | מטרה |
|------|------|
| `MondayItemMappings` | מקשר `TikCounter` ב-Odcanit ל-`ItemId` + `BoardId` ב-Monday |
| `SyncLogs` | רשומות לוג תפעוליות של סנכרון |
| `SyncFailures` | טבלת Dead-letter לפעולות סנכרון שנכשלו |
| `SyncRunMetrics` | מטריקות מצטברות לכל ריצה |
| `SyncRunLocks` | נעילה בשורה אחת למניעת ריצות חופפות |
| `ListenerStates` | watermark של change feed (חותמת זמן אחרונה שעובדה) |
| `MondayHearingApprovalStates` | עוקב אחר סטטוס אישור הגעה אחרון ידוע לכל פריט Monday |
| `HearingNearestSnapshots` | נתוני דיון קרוב ביותר ששמורים במטמון לזיהוי שינויים |
| `MondayDocumentImports` | מעקב צינור קליטת קבצים (מחזור חיים לכל asset) |
| `NispahWriteLogs` | ביקורת עמידה של כל כתיבות הנספחים ל-Odcanit |
| `NispahAuditLogs` | מסלול ביקורת כללי לפעולות נספח |
| `NispahDeduplications` | מניעת כפילויות לפי hash תוכן עבור כתיבות נספח |
| `CaseAnnexWriteStates` | דגלי אידמפוטנטיות לכל תיק (לדוגמה: סיפור תאונה נכתב) |
| `AllowedTiks` | רשימת TikCounters מורשים לטעינה מבוקרת |
| `EmailAlertDedups` | סכימה שמורה לטביעות אצבע עמידות; `EmailNotifier` הנוכחי משתמש במטמון suppression בזיכרון |
| `VoicenterApiRequestLogs` | רשומת ביקורת לכל קריאת API יוצאת ל-Voicenter, מופרדת לפי סוג נקודת קצה (למעקב מכסה) |
| `VoicenterQuotaWarningStates` | שורה אחת לכל (שבוע, סוג נקודת קצה) כאשר נשלח דוא"ל אזהרת מכסה — מונע ספאם אזהרות שבועיות |
| `VoicenterCallProcessingStates` | מטמון מצב סופי לכל CallID; מונע מה-Worker לשלוף שוב פרטים עבור שיחות שכבר טופלו |
| `NetCourtDecisionAlerts` | זהות החלטה עמידה, תוצאת ניתוב, מצב דוא"ל וסטטוס תור |
| `NetCourtDecisionAlertStates` | מצב אבחון שנשמר עבור אתחול/watermark של NetCourt |
| `EmailAutomationMailboxStates` | delta link ו-baseline לכל תיבה ותיקייה |
| `EmailAutomationLogs` | ביקורת מצומצמת להודעות מותאמות בלבד: תיבה, טביעות ממופתחות, `TikCounter`, עובד יעד, פעולה/תוצאה, קטגוריית שגיאה מנוטרלת וחותמות זמן; הודעות לא מותאמות אינן יוצרות שורה |

**אינדקסים על `MondayItemMappings`:** `TikCounter` ייחודי ומוגבל לערכים חיוביים. `(TikNumber, BoardId)` ו-`MondayItemId` הם אינדקסי חיפוש שאינם ייחודיים. אינדקס covering על `(BoardId, TikCounter)` תומך בקריאות מיפוי. הקריאות הן candidate-scoped עם `WITH (NOLOCK)` ובאצוות; מועמדים שנותרו unresolved לאחר ניסיונות מדורגים נדחים לריצה הבאה ואינם נחשבים ללא-ממופים.

### 6.2 בסיס נתוני Odcanit

גישת קריאה בלבד דרך Views של EF Core (ראו סעיף 5.2). גישת כתיבה רק דרך Stored Procedures.

### 6.3 מנגנוני מניעת כפילויות

ODMON משתמש במספר שכבות למניעת כתיבות כפולות:

| מנגנון | היקף | אופן פעולה |
|--------|------|------------|
| `NispahWriteLogs` | כל כתיבות הנספח (אישור הגעה, שיחות Voicenter) | שאילתה לפי `SourceKind + SourceItemId + TikCounter + !Failed` לפני כתיבה |
| `NispahDeduplications` | סיפור תאונה ונספחים כלליים | hash תוכן (`TikVisualID + NispahTypeName + InfoHash`) עם חלון זמן |
| `MondayDocumentImports` | קליטת מסמכים | מעקב לפי `(QuestionnaireItemId, ColumnId, AssetId)` עם מחזור חיי סטטוס |
| `CaseAnnexWriteStates` | סיפור תאונה לכל תיק | דגל בוליאני `AccidentStoryAnnexWritten` לכל `TikCounter` |
| `MondayHearingApprovalStates` | סנכרון אישור הגעה חי | `LastKnownStatus` מונע עיבוד חוזר של אותו סטטוס |
| `SyncRunLocks` | ריצות סנכרון | נעילה בשורה אחת עם תפוגה מונעת מחזורי סנכרון חופפים |
| checksum ב-`MondayItemMappings` | סנכרון שדות תיק | `OdcanitVersion` / `MondayChecksum` מונעים קריאות API מיותרות |

### 6.4 לוגי כתיבה וביקורת

**`NispahWriteLogs`** היא טבלת הביקורת הראשית לכל פעולות כתיבה חזרה ל-Odcanit:

| עמודה | מטרה |
|-------|------|
| `SourceKind` | מפריד: `"HearingApproval"`, `"VoicenterCall"` |
| `SourceItemId` | מזהה מקור (Monday item ID או hash של CallID) |
| `TikCounter` | תיק Odcanit |
| `Failed` | האם הכתיבה הצליחה או נכשלה |
| `ErrorMessage` | פרטי שגיאה בכשלון, הפניית CallID בהצלחה |
| `InfoHash` | hash SHA-256 של תוכן הנספח |
| `CreatedAtUtc` | חותמת זמן |

טבלה זו משרתת שתי מטרות: **מניעת כפילויות** (דילוג אם כבר נכתב) ו**ביקורת** (הוכחה ניתנת לחיפוש של כל כתיבה).

---

## 7. טיפול בשגיאות וחוסן

### בידוד לכל שיחה / לכל פריט

**במילים פשוטות:**  
אם פריט אחד נכשל בעיבוד, המערכת מדלגת עליו ועוברת לבא. תפוח רקוב אחד לא מקלקל את כל הסל.

**יישום בין פיצ'רים:**

| פיצ'ר | מנגנון בידוד |
|-------|-------------|
| עיבוד שיחות Voicenter | כל CallID עטוף ב-try/catch; כשלון מגדיל מונה, רושם שגיאה, ממשיך |
| קליטת מסמכים | כל asset מעובד עצמאית; כשלונות נרשמים ב-`MondayDocumentImports` עם ניסיון חוזר |
| התאמת סנכרון | try/catch לכל תיק; כשלונות נרשמים ב-`SyncFailures`; circuit breaker עוצר ריצה אם יותר מדי |
| אישור הגעה | עיבוד לכל פריט; חריגות נרשמות, מצב לא מתקדם בכשלון |

### Circuit Breaker (סנכרון)

אם כשלונות רצופים במהלך ריצת סנכרון חורגים מ-`Monday:CircuitBreakerFailureThreshold` (ברירת מחדל 10), הריצה מבוטלת מוקדם ומתועדת `CircuitBreakerTripped=true` ב-`SyncRunMetrics`.

### לוגיקת ניסיון חוזר

- **קליטת מסמכים**: עד `MaxRetryCount` (ברירת מחדל 3) ניסיונות חוזרים עם backoff מעריכי
- **קריאות Monday API**: `MaxRetryAttempts` (ברירת מחדל 3) עבור כשלונות GraphQL חולפים
- **Voicenter**: ללא ניסיון חוזר אוטומטי לכל שיחה; המחזור הבא יעבד מחדש אם טרם נכתב (בדיקת dedup)

### זיהוי כשלי חיבור SQL

`SqlConnectionFailureDetector` מסווג שגיאות SQL ל:
- timeout של שאילתה
- כשלי רשת
- כשלי חיבור

סיווג זה מנחה החלטות הסלמת התראות.

### בטיחות קריסה (Crash Safety)

- **קליטת מסמכים**: checkpoint לאחר `CreateDocumentRowAsync` → סטטוס `InProgress`. אם התהליך קורס לפני העברת הקובץ, הריצה הבאה מזהה את המצב הלא שלם ויכולה לנסות שוב או לדלג
- **סנכרון**: ל-`SyncRunLock` יש `ExpiresAtUtc` — אם Worker קורס באמצע ריצה, הנעילה פוקעת אוטומטית והמחזור הבא יכול להמשיך
- **Voicenter**: כתיבת נספח + `NispahWriteLog` ברצף. אם מתרחשת קריסה אחרי כתיבת Odcanit אבל לפני שמירת הלוג, המחזור הבא ינסה כתיבה כפולה (לא מזיק — ה-SP של Odcanit מטפל בכך בחן)

---

## 8. דוא"ל סיכום יומי

**במילים פשוטות:**  
כל בוקר, המערכת שולחת דוא"ל שמסכם את פעילות אתמול — כמה תיקים נוצרו, מסמכים עובדו, ושגיאות שדורשות תשומת לב.

**נשלח ב:** שעה ישראלית מוגדרת (`Email:DailySummaryTimeIsrael`, ברירת מחדל `"08:00"`)

**חלקים:**

1. **תיקים שנוצרו** — מספר ורשימת דוגמה של תיקים חדשים שנקלטו ב-Monday
2. **דיונים שסונכרנו** — מספר עדכוני דיונים שנדחפו ל-Monday
3. **פריטים שעודכנו** — סך פריטי Monday שעודכנו
4. **כשלונות סנכרון** — מקובצים לפי תיק וסיבת שורש, עם ספירות
5. **קליטת מסמכים** — מספר הצלחות וכשלונות מקובצים לפי actionable, timeout ישן, קובץ משתמש לא תקין, כשל חיצוני חולף, חסימה ידועה ובעיית נתונים ידועה
6. **סיכומי שיחות Voicenter** — מטריקות CDR/detail/dedup מהריצה האחרונה, כתיבות וכשלונות נספח, שימוש שבועי ב-`CallHistoryDetail` ומצב מכסה
7. **מצב מערכת ואנומליות** — circuit breaker, נפח כשלונות גבוה, כשלונות אמיתיים, בעיות קליטת מסמכים ובעיות מכסה/detail של Voicenter

`FailureClassifier` משתמש בערכי `ErrorType` ו-`Operation` מפורשים כדי להפריד כשלונות קריטיים/תפעוליים מבעיות נתונים ידועות ומדילוגים צפויים. פריט לא פעיל, חוסר ניתוב, קובץ מצורף גדול מדי ואירוע duplicate-idempotent אינם מדווחים ככשלונות אמיתיים.

**מקורות נתונים:**
- `MondayItemMappings` (נוצרו בחלון)
- `HearingNearestSnapshots` (סונכרנו בחלון)
- `SyncFailures` (בחלון)
- `MondayDocumentImports` (כשלו בחלון)
- `NispahWriteLogs` (כתיבות/כשלונות Voicenter בחלון)
- `SyncRunMetrics` (circuit breaker, ספירות כשלונות ריצה)
- `VoicenterCallSummaryWorker.LastRunResult` (מונים מהריצה האחרונה)

---

## 9. התראות וניטור

### דוא"ל התראות קריטיות

**במילים פשוטות:**  
המערכת שולחת התראות דוא"ל מיידיות כאשר משהו משתבש ברצינות, כמו קריסת Worker או כשל חיבור לבסיס נתונים.

**מנגנון התראות:** `IEmailNotifier.QueueCriticalAlert` → תור, מניעת כפילויות, הגבלת קצב על ידי `EmailNotifier`.

**הגבלת קצב:**
- `Email:MaxEmailsPerHour` (ברירת מחדל 10)
- `Email:DedupWindowMinutes` (ברירת מחדל 60)
- טביעות האצבע ומוני ה-suppression נשמרים בזיכרון התהליך ומתאפסים באתחול; `EmailAlertDedups` אינה משמשת את `EmailNotifier` הנוכחי
- ספציפי ל-Voicenter: `FailureAlertCooldownMinutes` (ברירת מחדל 60) להתראות ברמת Worker

**מקורות התראה:**

| מקור | מפעיל |
|------|-------|
| `SyncWorker` | כשלי חיבור SQL במהלך סנכרון |
| `VoicenterCallSummaryWorker` | חריגה לא מטופלת בורחת מהמחזור (`AlertOnUnhandledException`) |
| `VoicenterCallSummaryWorker` | אין מחזור מוצלח משך `StaleWorkerThresholdHours` (`AlertOnStaleWorker`) |
| `DocumentIngestionService` | כשלונות קליטה קריטיים (שגיאות שמצדיקות התראה) |
| `IErrorNotifier` | קריסת Worker, שיעור כשלונות גבוה |

### תקציר תקופתי (Digest)

`EmailBackgroundService` שולח תקציר תקופתי (בתדירות `Email:DigestIntervalMinutes`) רק כאשר במטמון ההתראות שבזיכרון קיימים אירועים שדוכאו עקב dedup או הגבלת קצב.

### לוגים מובנים

כל ה-Workers משתמשים בלוגים מובנים דרך Serilog עם:
- Sink קונסול (פיתוח)
- Sink קובץ מתגלגל (ייצור: `C:\Services\Odmon\logs\odmon-.log`, רוטציה יומית, שימור 14 יום, מגבלת 100 MB)
- קידומות לוג עקביות לכל פיצ'ר: `SYNC |`, `DOC_INGEST |`, `VOICENTER |`, `VOICENTER_WORKER |`, `HEARING_APPROVAL |`

---

## 10. קונפיגורציה ופריסה

### היררכיית קונפיגורציה

ערכי קונפיגורציה נפתרים בסדר הבא (מאוחר יותר דורס מוקדם יותר):

1. `appsettings.json` (בסיס)
2. `appsettings.{Environment}.json` (ספציפי לסביבה)
3. משתני סביבה
4. Azure Key Vault (כאשר `KeyVault:Enabled` מופעל)
5. `dotnet user-secrets` (פיתוח בלבד)

### חלקי קונפיגורציה

| חלק | מטרה | הגדרות מרכזיות |
|-----|------|----------------|
| `Monday` | קונפיגורציית לוח ועמודות Monday.com | `ApiToken`, `BoardId`, `CasesBoardId`, 90+ מזהי עמודות, `ReviveInactiveItems`, `MaxRetryAttempts`, `CircuitBreakerFailureThreshold` |
| `Sync` | התנהגות Worker סנכרון ראשי | `Enabled`, `DryRun`, `MaxItemsPerRun`, `IntervalSeconds`, `ListenerUpdateOnly`, `ListenerCreationCutoffDate` |
| `Onboarding` | תקופת המתנה לתיקים חדשים | `CoolingPeriodDays` (ברירת מחדל 3) |
| `OdcanitLoad` | Allowlist טעינת תיקים | `EnableAllowList`, `TikCounters[]`, `TikNumbers[]` |
| `OdcanitWrites` | בקרת כתיבה חזרה ל-Odcanit | `Enable`, `DryRun` |
| `MondayDocumentIngestion` | צינור קליטת מסמכים | `Enabled`, `BoardId`, `InboxPath`, `MaxFileSizeBytes`, `AllowedExtensions[]`, `DeniedExtensions[]`, `Columns[]`, `ColumnMaxFileSizeBytes{}`, `IntervalSeconds`, `MaxRetryCount` |
| `MondayDocumentIngestion:AccidentStory` | נספח סיפור תאונה | `Enabled`, `WriteEnabled`, `NispahType`, `Columns[]` |
| `MondayDocumentIngestion:TasksSource` | קליטה מלוח משימות | `Enabled`, `BoardId`, `FileColumnId`, `TikNumberColumnId`, `TaskStatusColumnId`, `SuccessStatusLabel` |
| `OdcanitDocuments` | ברירות מחדל ל-SP מסמכי Odcanit | `CategoryCounter`, `SubCategoryCounter`, `DocStatus`, `DocType`, `WriterCounter`, `OwnerCounter`, `Metapel` |
| `NispahWriter` | הגנות כתיבת נספח | `MaxCreatesPerRun`, `MaxCreatesPerMinute`, `DeduplicationWindowMinutes`, `CommandTimeoutSeconds` |
| `VoicenterCallSummaries` | אינטגרציית Voicenter | `Enabled`, `IntervalHours`, `LookbackHours`, `NispahTypeName`, `OnlyAnsweredCalls`, `MinimumDurationSeconds`, `ThrottleMs`, `TestMode`, `TestCallId`, `AlertOnUnhandledException`, `AlertOnStaleWorker`, `StaleWorkerThresholdHours`, `FailureAlertCooldownMinutes`, `WeeklyUsageWarningThreshold`, `WeeklyUsageHardLimit`, `UsageWarningEmailEnabled` |
| `VoicenterBackfill` | Backfill חד-פעמי לסיכומי שיחות Voicenter (שחזור שיחות שהוחמצו לאחר הפסקת מכסה) | `Enable`, `FromUtc`, `ToUtc`, `MaxCalls`, `ForceRecheck`, `DryRun` |
| `HearingNearestSchedule` | reconciliation עצמאי של הדיון הקרוב בשעות קבועות | `Enabled`, `BoardId`, `TimesIsrael[]` |
| `NetCourtDecisionAlerts` | התראות החלטה מ-Odcanit לדוא"ל | `Enabled`, `IntervalSeconds`, `StartFromDocDate`, `MaxBatchSize`, אימות קובץ, `EmailMode`, ניתוב ו-BCC |
| `EmailAutomation` | ניתוב/העברת תיבות Microsoft Graph | `Enabled`, `DryRun`, `RealForwardEnabled`, תדירות/מגבלות, credentials, הסוד `FingerprintKey`, baseline, תיבות וכללים |
| `Email` | SMTP והתראות | `Enabled`, `SmtpHost`, `SmtpPort`, `UseTls`, `Username`, `Recipients[]`, `MaxEmailsPerHour`, `DedupWindowMinutes`, `DigestIntervalMinutes`, `DailySummaryTimeIsrael` |
| `HearingBackfill` | ייבוא דיונים מרוכז | `Enable`, `SourceTable`, `BoardId`, `BatchSize` |
| `HearingApprovalBackfill` | Backfill אישורי הגעה היסטוריים | `Enable`, `DryRun`, `MaxItems`, `OnlyTikCounters`, `ThrottleMs` |
| `Testing` | מקור תיקי בדיקה | `Enable`, `Source`, `TikCounters[]`, `TableName` |
| `OdmonTestCases` | תיקי בדיקה E2E | `Enable`, `MaxId`, `OnlyIds` |
| `Safety` | הגנות נתוני בדיקה | `TestBoardId`, `AllowedTikNamePrefix`, `AllowedTikNumberPrefixes[]`, `AllowedTikCounters[]` |
| `Serilog` | לוגים מובנים | Sinks (קונסול, קובץ), רמות מינימום, דריסות, רוטציית קבצים |
| `ConnectionStrings` | חיבורי בסיס נתונים | `IntegrationDb`, `OdcanitDb` (הפניות למפתח, לא מחרוזות ממשיות) |
| `KeyVault` | Azure Key Vault | `VaultUrl`, `Enabled` (בקוד, לא תמיד ב-JSON) |
| `Secrets` | fallback סודות מקומיים | `Monday__ApiToken`, מפתחות מחרוזות חיבור (לפיתוח מקומי בלבד) |

### פריסה

- **Runtime:** .NET 8, נפרס כשירות Windows (`UseWindowsService()`)
- **מיגרציות בסיס נתונים:** EF Core Code First על `IntegrationDbContext`; יש להריץ `dotnet ef database update` לפני פריסה
- **מערכת קבצים:** דורש הרשאות כתיבה לנתיב inbox (`MondayDocumentIngestion:InboxPath`) ולתיקיות מסמכי Odcanit
- **תיקיית לוגים:** `C:\Services\Odmon\logs\` (מוגדרת דרך Serilog)
- **רשת:** HTTPS יוצא ל-Monday.com API, Voicenter API, ו-SMTP (Office365); חיבורי SQL Server נכנסים לשני בסיסי הנתונים

---

## 11. ניהול סודות

**במילים פשוטות:**  
סיסמאות ומפתחות API לעולם לא מאוחסנים בקובץ הקונפיגורציה. הם נשמרים בכספת מאובטחת ונטענים רק בזמן ריצה.

**ארכיטקטורה:** `ISecretProvider` עם דפוס `CompositeSecretProvider`.

**שרשרת ספקים (לפי סדר עדיפות):**

| ספק | סביבה | מקור |
|-----|-------|------|
| `UserSecretsProvider` | פיתוח בלבד | `dotnet user-secrets` → `Secrets:{key}` בקונפיגורציה |
| `AzureKeyVaultSecretProvider` | כאשר `KeyVault:Enabled` | Azure Key Vault דרך `SecretClient` |
| `EnvironmentSecretProvider` | תמיד | `Environment.GetEnvironmentVariable(key)` |

**סודות נדרשים:**

| מפתח | מטרה |
|------|------|
| `Monday__ApiToken` | אימות Monday.com API |
| `IntegrationDb__ConnectionString` | חיבור לבסיס נתוני אינטגרציה |
| `OdcanitDb__ConnectionString` | חיבור לבסיס נתוני Odcanit |
| `Voicenter__Code` | קוד CDR API של Voicenter |
| `Voicenter__BearerToken` | Bearer token ל-API פרטי שיחות Voicenter |
| `Email:Username` / סיסמת SMTP | שליחת דוא"ל (נפתר מקונפיגורציה, לא ממאגר סודות) |

**אימות עליה:** `ValidateRequiredSecretsAsync` בודק סודות קריטיים בעלייה ונכשל מהר אם חסרים.

---

## 12. מצבי בדיקה

**במילים פשוטות:**  
למערכת יש מצבי בטיחות מובנים לבדיקות — ניתן להריץ אותה מול נתונים מלאכותיים או להגביל אותה לתיקים ספציפיים, כך שלעולם לא תשנה בטעות תיקים אמיתיים במהלך פיתוח.

### מקורות תיקי בדיקה

| מצב | קונפיגורציה | התנהגות |
|-----|-------------|---------|
| ייצור | ברירת מחדל | `OdcanitCaseSource` קורא מ-Views אמיתיים של Odcanit |
| בדיקת אינטגרציה | `Testing:Enable=true` | `IntegrationTestCaseSource` קורא מטבלה מוגדרת ב-Integration DB |
| בדיקה E2E | `OdmonTestCases:Enable=true` | `OdmonTestCasesReader` קורא מ-`dbo.OdmonTestCases` עם מיפוי עמודות עברית |

### הגנות בטיחות

- `TestSafetyPolicy` מגביל סנכרון ל-TikCounters/TikNumbers/קידומות שם מורשים
- `GuardOdcanitReader` עוטף את הקורא האמיתי וזורק חריגה בכל קריאה כאשר מצב בדיקה חוסם גישה ל-Odcanit
- `Safety:AllowedTikNumberPrefixes` (לדוגמה `["9/999"]`) מונע כתיבה בטעות לתיקים בייצור
- `OdcanitWrites:DryRun` שולט בכתיבות אישור הגעה ובמסלול הדיון הקרוב; הוא אינו משבית את קליטת המסמכים מ-Monday
- `Sync:DryRun` משבית מוטציות Monday API

### מצב בדיקת Voicenter

כאשר `VoicenterCallSummaries:TestMode=true` ו-`TestCallId` מוגדר, רק אותה שיחה בודדת מעובדת עם לוגים אבחוניים מורחבים. זהו מסנן לשיחה חיה ולא מצב שמונע כתיבה. לתצוגה מקדימה ללא כתיבה יש להשתמש ב-`VoicenterBackfill:DryRun=true`.

---

## נספח: מבנה קבצים

```
Odmon/
├── Configuration/          # מחלקות הגדרות (מקושרות מ-appsettings.json)
├── Data/                   # IntegrationDbContext, MondayMappingReadService
├── docs/                   # תיעוד זה ורשימות ספציפיות לפיצ'רים
├── Migrations/             # מיגרציות EF Core עבור IntegrationDb
├── Models/                 # מודלי ישויות (אינטגרציה + Views של Odcanit)
├── Monday/                 # לקוח Monday.com, מטא-דאטה, הגדרות
├── OdcanitAccess/          # DB context של Odcanit, קוראים, כותבים
├── Security/               # מימושי ISecretProvider
├── Services/               # שירותי לוגיקה עסקית
├── Voicenter/              # לקוח API ומודלים של Voicenter
├── Workers/                # Workers של שירותי רקע
├── Program.cs              # בניית Host, רישום DI, אימות עליה
├── appsettings.json        # קונפיגורציית בסיס
└── appsettings.Development.json  # דריסות פיתוח
```

---

מסמך זה מתעדכן בהתאם לשינויים בקוד ובלוגיקה העסקית.
