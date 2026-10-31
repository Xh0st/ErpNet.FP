<img src="https://github.com/erpnet/ErpNet.FP/raw/master/ErpNet.FP.Server/ErpNet.FP.thumb.png" align="right"/>

# ErpNet.FP – ediția pentru România

ErpNet.FP este un server HTTP mic, multi-platformă, prin care un POS sau ERP tipărește la casa de marcat
printr-un API JSON simplu. Serverul detectează aparatele, citește starea lor, tipărește bonuri, rapoarte
și alte documente, și răspunde cu rezultatul fiecărei operații.

Acest fork pornește de la [erpnet/ErpNet.FP](https://github.com/erpnet/ErpNet.FP) și adaugă suport pentru
**casele de marcat Datecs cu firmware românesc** (DP-25, DP-25MX, DP-150MX, WP-50MX, WP-500MX, FP-700MX, FMP-350MX),
plus câteva corecturi pentru folosirea serviciului ca **bridge local sincron** între aplicație și casa de marcat.

➡️ Ce s-a schimbat față de versiunea originală, configurarea casei și testarea pe aparat: **[README-RO.md](README-RO.md)**.
Documentația originală în engleză: [README.en.md](README.en.md).

Serverul primește cererile prin API-ul REST și comunică cu aparatele prin:
* port serial COM (inclusiv USB cu port COM virtual);
* Bluetooth mapat pe un port COM;
* TCP/IP (rețea).

### Direct la [**Descărcare**](#descărcare)

# Protocolul Net.FP

Toată comunicarea cu serverul folosește protocolul Net.FP (Net Fiscal Protocol), bazat pe JSON.

## Cereri

Exemplul de mai jos tipărește un bon pe imprimanta `casa1`. Identificatorul imprimantei (printerId) apare în
lista întoarsă de `GET /printers` (vezi [Identificatorul imprimantei](#identificatorul-imprimantei)).

`POST /printers/casa1/receipt`

```json
{
  "uniqueSaleNumber": "POS1-000123",
  "items": [
    {
      "text": "Schimb ulei",
      "quantity": 1,
      "unitPrice": 120,
      "taxGroup": 1,
      "unit": "buc."
    },
    {
      "type": "comment",
      "text": "Auto B-123-ABC"
    },
    {
      "text": "Ulei motor 5W30",
      "quantity": 4.5,
      "unitPrice": 40,
      "taxGroup": 1,
      "unit": "l",
      "priceModifierValue": 10,
      "priceModifierType": "discount-percent"
    },
    {
      "type": "footer-comment",
      "text": "VA MULTUMIM!"
    }
  ],
  "payments": [
    { "amount": 200, "paymentType": "card" },
    { "amount": 100, "paymentType": "cash" },
    { "amount": -18, "paymentType": "change" }
  ]
}
```

Câmpuri specifice ediției românești:
* `unit` – unitatea de măsură a articolului (implicit `buc.`), obligatorie pe bonul fiscal românesc;
* `uniqueSaleNumber` – identificatorul vânzării din aplicația voastră (1–64 caractere: litere, cifre, `. _ : / -`).
  Nu se tipărește pe bon; garantează că un bon retrimis cu același identificator nu iese de două ori.

## Mai multe informații

Descrierea completă a protocolului (în engleză): [PROTOCOL.md](PROTOCOL.md).

## Răspunsul – cum se interpretează

Cel mai important câmp este `ok`: `true` dacă operația a reușit, `false` dacă nu.
Dacă `ok` este `false`, răspunsul conține sigur cel puțin un mesaj de tip `error`.

Codurile de eroare și avertizare sunt aceleași pentru toți producătorii; lista lor este în
[ErrorCodes.md](ErrorCodes.md). Ele sunt un subset al codurilor fiecărui producător. Când există, codul original
al aparatului este în câmpul `originalCode`, dar acesta diferă de la un producător la altul și chiar între
versiuni de firmware, de aceea aplicația ar trebui să se bazeze pe codurile standard.
Mesajele de tip `info` nu au cod, pentru că nu pot fi standardizate.

### Exemplu de răspuns fără probleme, după tipărirea unui bon

```json
{
  "ok": "true",
  "messages": [],
  "receiptNumber": "0000085",
  "receiptDateTime": "2026-10-31T13:55:18",
  "receiptAmount": 282,
  "fiscalMemorySerialNumber": "DA4600123456"
}
```

### Exemplu de răspuns cu avertizare, la citirea stării

```json
{
  "ok": "true",
  "messages": [
    {
      "type": "warning",
      "code": "W301",
      "text": "Near paper end"
    }
  ],
  "deviceDateTime": "2026-10-31T15:50:00"
}
```

### Exemplu de răspuns cu eroare

```json
{
  "ok": "false",
  "messages": [
    {
      "type": "error",
      "code": "E301",
      "text": "End of paper"
    }
  ]
}
```

# Descărcare

Versiunile oficiale de pe pagina proiectului original **nu conțin** driverul românesc.
Folosiți build-ul acestui fork:

* **Windows 64 bit** – artefactul `ErpNet.FP-RO-win-x64` din ultima rulare reușită a
  [GitHub Actions](https://github.com/Xh0st/ErpNet.FP/actions) pe branch-ul `romania-datecs`.
  Nu este nevoie de .NET instalat; totul este inclus.

Dezarhivați într-un folder (de exemplu `C:\ErpNet.FP`) și porniți `ErpNet.FP.Server.exe`, sau înregistrați-l
ca serviciu Windows:

```powershell
sc.exe create ErpNet.FP binPath= "C:\ErpNet.FP\ErpNet.FP.Server.exe" start= auto
sc.exe start ErpNet.FP
```

Cu configurația implicită, pagina de administrare este la http://localhost:8001.

Pentru alte sisteme (Windows 32 bit, Linux, Linux ARM / Raspberry Pi, macOS) compilați din sursă,
vezi [Cod sursă](#cod-sursă). Pe Linux, utilizatorul serviciului trebuie să aibă drept de citire și scriere
pe porturile seriale (sau serviciul rulează ca root).

## Informații de depanare

Pagina de administrare are o legătură către `debug.log` și folderul lui. Logul conține fiecare cadru trimis
către casă (`<<<`) și fiecare răspuns (`>>>`), deci arată exact ce a răspuns aparatul la o comandă refuzată.

## Configurarea serverului

Opțiunile se află în fișierul `appsettings.json`, lângă executabil. Descrierea completă (în engleză):
[Config.md](Config.md). Exemplu pentru România:

```json
{
  "ErpNet.FP": {
    "AutoDetect": false,
    "Printers": {
      "casa1": { "Uri": "ro.dt.x.isl.com://COM3" },
      "casa2": { "Uri": "ro.dt.x.isl.tcp://192.168.1.50:3999" }
    }
  }
}
```

Opțiunile per aparat (unitate de măsură implicită, forme de plată, jurnalul USN) sunt descrise în
[README-RO.md](README-RO.md#configurare-appsettingsjson). Se recomandă modificarea setărilor din pagina de
administrare sau prin API-ul `/service`, nu direct în fișier.

## Pregătirea casei de marcat

Casa trebuie să fie în modul care acceptă comenzi de la calculator. La Datecs MX acesta este **modul 6
„Conexiune PC”**, cu portul și viteza setate în meniul „Conexiune cu PC”. Pașii exacți sunt în
[README-RO.md](README-RO.md#pregătirea-casei-de-marcat-dp-25mx-dp-150mx-wp-500mx-fp-700mx).

# Concepte

## Identificatorul imprimantei

Fiecare imprimantă are un identificator (printerId):

1. Pentru aparatele detectate automat, identificatorul este seria aparatului, de exemplu `da4600123456`.
2. Dacă același aparat este legat prin mai multe transporturi (de exemplu COM și Bluetooth), următoarele
   conexiuni primesc un număr: `da4600123456_1`. Ordinea este mereu aceeași, dar se recomandă folosirea
   identificatorului fără număr.
3. Pentru aparatele din `appsettings.json`, identificatorul este numele din configurație, de exemplu `casa1`.

## URI-ul imprimantei

URI-ul conține informațiile de conectare: driverul, transportul și adresa. Exemple:
- `ro.dt.x.isl.com://COM3`
- `ro.dt.x.isl.com://COM3:9600` (cu viteză explicită)
- `ro.dt.x.isl.tcp://192.168.1.50:3999`
- `bg.zk.zfp.tcp://192.168.1.60:4444`

URI-ul este folosit intern de server, dar apare și în informațiile despre aparat.

# Aparate și sisteme suportate

Serverul rulează pe Windows 32/64 bit, Linux x64, Linux ARM și macOS.

## Driverul românesc

* `ro.dt.x.isl` – Datecs cu firmware românesc: DP-25, DP-25MX, DP-150MX, WP-50MX, WP-500MX, FP-700MX, FMP-350MX.
  Formatul comenzilor a fost verificat pe un DP-25 românesc; modelele MX trebuie confirmate pe aparat cu
  scriptul `tools\test-ro.ps1` (vezi [README-RO.md](README-RO.md#test-pe-aparat-real)).

## Driverele bulgărești moștenite

Driverele de mai jos vin din proiectul original și sunt scrise pentru firmware-ul bulgăresc. Pot funcționa cu
aparate românești doar dacă firmware-ul acestora folosește același protocol, ceea ce trebuie verificat pe aparat.

* `bg.dt.c.isl` – Datecs WP-50, DP-05, DP-05B, DP-05C, DP-25, DP-35, DP-150, DP-15
* `bg.dt.p.isl` – Datecs FP-650, FP-800, FP-2000, FMP-10, SK1-21F, SK1-31F
* `bg.dt.x.isl` – Datecs DP-25X, FMP-350X, FP-700X, WP-500X, FMP-55X, WP-50X, DP-150X, WP-25X, FP-700XE
* `bg.zk.zfp` – Tremol A19Plus, S21, M23, M20, FP15, SB, S25, FP24
* `bg.zk.v2.zfp` – Tremol Z-KL-V2, ZM-KL-V2, ZS-KL-V2, FP01-KL V2, FP05-KL V2, M-KL-V2, S-KL-V2, FP15 KL V2, FP03-KL V2, FP07-KL V2, FP01, FP21
* `bg.ed.isl` – Eltrade A1 KL, A3 KL, B1 KL, PRP 250F KL, A6 KL, B3 KL, EPSON TM-T810F KL, EPSON TM-T81F KL
* `bg.dy.isl` – Daisy Compact S, Compact M, eXpert SX, Perfect M, MICRO C, eXpert 01, Perfect S, FX 1300, FX 1200C, Perfect SA, FX 21
* `bg.in.isl` – Incotex 133 KL-Q, 181 KL-Q, 777, 300SM KL-Q, 300S KL-Q
* `bg.is.icp` – ISL5011S-KL
* `bg.sis.json` – modul fiscal SIS (configurat explicit, nu se detectează automat)

Atenție la Tremol: driverele `bg.zk.*` acceptă `uniqueSaleNumber` doar gol sau în formatul bulgăresc
(`XX123456-XXXX-1234567`).

## Rapoarte

* Raport X și raport Z: `POST /printers/{id}/xreport`, `POST /printers/{id}/zreport`.
  Pe Datecs MX raportul Z este permis doar operatorilor 26–30; trimiteți `operator` și `operatorPassword` în cerere.
* Raport din memoria fiscală pe perioadă: `POST /printers/{id}/periodreport`, varianta scurtă sau detaliată.
  Un raport detaliat pe o perioadă lungă ține casa ocupată minute întregi; trimiteți-l asincron
  (`asyncTimeout=0`) și urmăriți sarcina, în loc să așteptați timeout-ul implicit de 29 de secunde.

## Facturi și note de credit

Endpoint-urile `POST /printers/{id}/invoice` și `/creditnote` sunt implementate doar pentru driverul SIS
(`bg.sis.json`). Pentru celelalte drivere, inclusiv cel românesc, întorc eroarea `E413`.

## Parole implicite

Dacă cererea nu conține `operator` și `operatorPassword`, se folosesc:
* `ro.dt.x.isl` – operator `1`, parola `0001` (valoarea din fabrică pe casele Datecs românești)
* `bg.dt.c.isl` – operator `1`, parola `1`
* `bg.dt.p.isl`, `bg.dt.x.isl`, `bg.zk.zfp`, `bg.zk.v2.zfp` – operator `1`, parola `0000`
* `bg.ed.isl`, `bg.is.icp`, `bg.sis.json` – operator `1`, fără parolă
* `bg.dy.isl` – bonuri: operator `1`, parola `1`; stornări: operator `20`, parola `9999`
* `bg.in.isl` – operator `1`, parola `1`

# Cod sursă

Pentru compilare este nevoie de **.NET 8 SDK**.

```bash
dotnet build ErpNet.FP.Server/ErpNet.FP.Server.csproj -c Release
dotnet test ErpNet.FP.Core.Tests/ErpNet.FP.Core.Tests.csproj
dotnet publish ErpNet.FP.Server/ErpNet.FP.Server.csproj -c Release -r win-x64 --self-contained true -o out/win-x64
```

Pentru `-r` se poate folosi și `win-x86`, `linux-x64`, `linux-arm` sau `osx-x64`.
La fiecare push, GitHub Actions compilează proiectul, rulează testele și publică build-ul pentru Windows x64.

Ca IDE se pot folosi Visual Studio 2022 sau Visual Studio Code.

# Licență

„BSD Zero Clause License”, vezi [LICENSE.txt](LICENSE.txt).

# Contribuții

Vezi [CONTRIBUTING.md](CONTRIBUTING.md) și [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
