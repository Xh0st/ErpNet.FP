# ErpNet.FP pentru casele de marcat din România

Acest fork adaugă driverul **`ro.dt.x.isl`** pentru casele de marcat Datecs cu firmware românesc
(DP-25, DP-25MX, DP-150MX, WP-50MX, WP-500MX, FP-700MX, FMP-350MX …) și câteva corecturi generale
pentru folosirea serviciului ca bridge local sincron între POS/ERP și casa de marcat.

Driverele bulgărești (`bg.*`) rămân neschimbate, deci același build deservește toți clienții;
diferența se face doar din `appsettings.json`.

## Ce s-a schimbat

| Zonă | Schimbare | De ce |
|---|---|---|
| Driver nou `ro.dt.x.isl` | Comenzile Datecs X adaptate pentru România (vezi mai jos) | Upstream #159: niciun driver standard nu merge cu DP-25 RO |
| Comanda 48 (deschidere bon) | Fără câmpul УНП (număr unic de vânzare bulgăresc), un câmp gol în plus | Format verificat pe DP-25 RO (ridvandev 2ed544b) |
| Comanda 49 (vânzare) | Se trimite unitatea de măsură, implicit `buc.`; per articol prin câmpul `unit` | UM este obligatorie pe bonul românesc |
| Comanda 53 (plată) | `{Mod}\t{Sumă}\t`, fără câmpul `{Type}` bulgăresc | Formatul BG era refuzat de DP-25 RO |
| Comanda 76 (stare bon) | Suma se citește din coloana 7, cu verificare față de totalul calculat; dacă nu se poate citi, bonul nu mai este anulat | Coloana diferă față de BG |
| Comanda 90 (info aparat) | Serie de orice lungime, număr memorie fiscală opțional (se folosește seria) | Seriile RO nu au 8 caractere, FM poate lipsi |
| Parola operator implicită | `0001` | Valoarea din fabrică în România |
| Plăți mixte (toate driverele ISL) | Plățile non-cash se trimit primele, cash-ul ultimul; `change` și sumele 0 nu se trimit | Upstream #97: card + cash + rest bloca bonul în „General error” |
| Transport TCP | Timeout de conectare 3 s (era 200 ms), timeout de citire 2 s (era infinit), adresa nu mai rămâne blocată până la restart după un timeout, reconectare când aparatul închide conexiunea | Upstream #191: DP-150 pe TCP nu termina handshake-ul |
| Idempotență USN | Jurnal persistent pe disc: același `uniqueSaleNumber` nu se tipărește de două ori | Retry-ul clientului după timeout HTTP ar fi tipărit al doilea bon fiscal |

## Configurare (`appsettings.json`)

```json
{
  "ErpNet.FP": {
    "AutoDetect": false,
    "Printers": {
      "casa1": { "Uri": "ro.dt.x.isl.com://COM3" },
      "casa2": { "Uri": "ro.dt.x.isl.tcp://192.168.1.50:3999" },
      "tremol": { "Uri": "bg.zk.zfp.tcp://192.168.1.60:4444" }
    },
    "PrintersProperties": {
      "DA4600123456": {
        "PrinterOptions": {
          "itemUnit": "buc.",
          "receiptAmountField": "7",
          "usnJournal": "true",
          "usnJournalPath": "C:\\ErpNet.FP\\usn-journal\\DA4600123456.json"
        },
        "PaymentTypeMappings": {
          "card": "1",
          "coupons": "3"
        }
      }
    }
  }
}
```

Cheia din `PrintersProperties` este **seria aparatului** (o vezi în `GET /printers`).
Toate opțiunile sunt opționale:

* `itemUnit` – UM implicită când articolul nu are `unit` (implicit `buc.`).
* `receiptAmountField` – coloana sumei din răspunsul comenzii 76, dacă firmware-ul tău o are în altă parte.
* `usnJournal` – `false` dezactivează protecția la dublă tipărire.
* `usnJournalPath` – unde se ține jurnalul (implicit `usn-journal\<serie>.json` lângă executabil).
* `PaymentTypeMappings` – implicit sunt permise doar `cash` (0) și `card` (1). Pentru alte forme de plată
  (ex. pe MX: 2 credit, 3 tichete masă, 4 tichete valorice, 5 voucher) adăugați maparea aici, ex. `"coupons": "3"`.
  Fără mapare, un bon cu altă formă de plată e refuzat cu `E406` înainte să ajungă la aparat.

Cu `AutoDetect: true`, driverul românesc este încercat după cel bulgăresc și refuză aparatele cu firmware bulgăresc.
Pentru producție recomand totuși URI-uri explicite.

## Pregătirea casei de marcat (DP-25MX, DP-150MX, WP-500MX, FP-700MX)

Din manualul DP-25MX (Datecs România), meniul de programare, secțiunea „Conexiune cu PC”:

1. **Port**: `1` COM1, `2` COM2, `3` USB, `5` LAN – alegeți portul pe care e legat calculatorul.
2. **Viteză**: valoarea implicită din fabrică este `0` = 1200 bps. Setați `9` = 115200 bps (viteza implicită ErpNet.FP),
   sau păstrați viteza aparatului și puneți-o în URI: `ro.dt.x.isl.com://COM3:9600`.
3. Pentru LAN: setați IP fix (sau rezervare DHCP) și folosiți `ro.dt.x.isl.tcp://IP:3999`.
4. Treceți casa în **modul 6 „Conexiune PC”** înainte de a porni serviciul. În alt mod casa nu răspunde la comenzi.
5. Operatorul folosit de bridge este 1 cu parola `0001` (implicit din fabrică). Dacă ați schimbat parola,
   trimiteți `operator` / `operatorPassword` în cerere. Raportul Z pe MX e permis doar operatorilor 26–30.

## Bon cu UM și USN

```http
POST http://localhost:8001/printers/casa1/receipt
Content-Type: application/json

{
  "uniqueSaleNumber": "POS1-000123",
  "items": [
    { "text": "Paine", "quantity": 2, "unitPrice": 4.50, "taxGroup": 1 },
    { "text": "Mere",  "quantity": 1.25, "unitPrice": 6.00, "taxGroup": 2, "unit": "kg" }
  ],
  "payments": [
    { "amount": 10.00, "paymentType": "card" },
    { "amount": 20.00, "paymentType": "cash" },
    { "amount": -13.50, "paymentType": "change" }
  ]
}
```

`uniqueSaleNumber` este identificatorul vostru (1–64 caractere: litere, cifre, `. _ : / -`).
Nu se trimite la casa de marcat; este folosit doar pentru idempotență:

* același USN, bon tipărit deja → răspuns cu aceleași date (număr bon, sumă, dată), fără a tipări din nou;
* bon eșuat și anulat pe aparat → USN-ul se poate retrimite;
* bon întrerupt (cădere de curent, conexiune pierdută în timpul tipăririi) → eroare `E409`, nu se retipărește automat.
  Verificați ultimul bon pe aparat și retrimiteți cu un USN nou doar dacă nu a ieșit.

## Test pe aparat real

`tools\test-ro.ps1` trimite 6 bonuri cu sume de 0.10–0.50 lei prin serviciul pornit local și verifică răspunsurile
(bon simplu, card + cash + rest, comentarii + UM kg + discounturi, USN trimis de două ori, numerar în sertar,
opțional raport X). **Pe un aparat fiscalizat sunt vânzări reale.**

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-ro.ps1 -Printer casa1 -WithXReport
```

Scriptul scrie `test-ro-<data>.log` lângă el; dacă un pas pică, logul plus logul serviciului arată ce a răspuns casa.

## Ce trebuie verificat pe aparatul real

Formatul comenzilor 48/49/53/76/90 vine dintr-un fix verificat pe un DP-25 românesc. Pentru restul modelelor
și pentru comenzile de mai jos nu am avut documentația Datecs RO, deci trebuie testate pe aparat:

1. Bon simplu cash, bon cu card + cash + rest, bon cu discount pe articol și pe subtotal.
2. Comentarii (`comment`, `footer-comment`) – comanda 54.
3. Storno (`reversalreceipt`) – folosește încă formatul bulgăresc (comanda 43).
4. Raport X, raport Z, intrare/ieșire numerar, duplicat.
5. Pe TCP: oprește aparatul în timpul serviciului și pornește-l din nou – trebuie să se reconecteze singur în ~30 s.

Logurile serviciului conțin fiecare cadru trimis (`<<<`) și primit (`>>>`); dacă o comandă e refuzată,
cadrul din log arată exact ce a răspuns aparatul.
