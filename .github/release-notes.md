ErpNet.FP pentru casele de marcat din România.

**Pre-release:** formatul comenzilor vine dintr-un fix verificat pe un Datecs DP-25 românesc. Modelele MX și comentariile pe bon trebuie confirmate pe aparat cu `tools\test-ro.ps1` înainte de folosirea la clienți.

## Noutăți în această versiune

- **Bon fiscal cu CUI cumpărător (factură simplificată) — validare.** `POST /invoice` pe Datecs RO și Tremol RO acceptă blocul `recipient`: `name` și `identifier` obligatorii (`E405`), CUI validat cu cifra de control (cheia 753217532, cu sau fără `RO`), `identifierType` `tax-number`/`legal-registration` acceptate iar `national-id`/`foreigner-id` respinse cu `E412`, numărul atribuit de casă (`device-assigned`, `number` trimis → `E412`). Emiterea CUI-ului pe casă se activează per model după confirmarea din documentația oficială de protocol (vezi `Drivers/*/Docs`).

- **Tremol RO (`ro.zk.zfp`): parola operatorului** se trimite pe 4 caractere, completată cu spații, cum o păstrează casa (`0` → `0   `). Parola implicită pentru Tremol RO este acum `0` (valoarea din fabrică). Cu v1.1.0-ro.3 casa refuza deschiderea bonului cu „Incorrect password”.
- Confirmat pe Tremol S25 RO (`TRM25-RO`): deschidere bon, articol cu UM, grupa TVA A, subtotal, anulare.

## Versiunea anterioară (v1.1.0-ro.3)

- **Driver nou `ro.zk.zfp` pentru Tremol cu firmware românesc** (S25 RO și celelalte modele „-RO”), scris după protocolul oficial Tremol RO (ZFP SDK 2601151250):
  - detectare automată pe COM/USB (casa trebuie să fie în **REG OPER**);
  - grupe TVA A–E și F (Alte taxe), unitate de măsură pe articol (implicit `buc`);
  - plăți: numerar și card (numărul plății „CARD” e citit din casă), card înaintea cash-ului;
  - comentarii, discount/adaos pe articol și pe subtotal, intrări/ieșiri numerar, rapoarte X/Z, duplicat;
  - fără bonuri duble pe același `uniqueSaleNumber`;
  - stornarea nu este implementată pentru Tremol (răspunde cu `E413`).

## Noutăți

- **Driver nou `ro.dt.x.isl`** pentru Datecs cu firmware românesc (DP-25, DP-25MX, DP-150MX, WP-50MX, WP-500MX, FP-700MX, FMP-350MX), separat de driverele bulgărești:
  - bon fără numărul unic de vânzare bulgăresc;
  - unitate de măsură pe fiecare articol (implicit `buc.`, câmpul nou `unit`);
  - plată fără câmpul bulgăresc `Type`; implicit doar `cash` și `card`, restul prin configurare;
  - serie de orice lungime, număr de memorie fiscală opțional;
  - parola implicită `0001`.
- **Plăți mixte:** cardul se trimite înaintea cash-ului, deci card + cash + rest nu mai blochează bonul (upstream #97).
- **Conexiune TCP mai robustă:** timeout-uri rezonabile, reconectare automată, adresa nu mai rămâne blocată până la restart (upstream #191).
- **Fără bonuri duble:** același `uniqueSaleNumber` retrimis după un timeout nu se mai tipărește a doua oară.
- Documentație în română (`README.md`, `README-RO.md`) și script de test pe aparat (`tools\test-ro.ps1`).

## Instalare (Windows)

1. Descărcați `ErpNet.FP-RO-<versiune>-setup.exe` și rulați-l ca administrator.
   Installer-ul copiază serverul în `C:\Program Files\ErpNet.FP`, instalează serviciul Windows „ErpNet.FP - Fiscal Print Server (RO)” cu pornire automată și deschide pagina de administrare (http://localhost:8001).
   Opțional, deschide portul 8001 în Windows Firewall, dacă aplicația rulează pe alt calculator.
2. Setați `appsettings.json` din `C:\Program Files\ErpNet.FP` (exemplu în `README-RO.md`), de exemplu `"Uri": "ro.dt.x.isl.com://COM3"`, apoi reporniți serviciul.
3. Treceți casa în modul 6 „Conexiune PC”.

La actualizare rulați noul `setup.exe` peste cel vechi: `appsettings.json`, jurnalul USN și logurile se păstrează. La dezinstalare serviciul este șters, dar configurația și jurnalul rămân pe disc.

Fără installer: zip-urile conțin aceleași fișiere, pentru Windows 64/32 bit, Linux x64 și Linux ARM. Nu este nevoie de .NET instalat.
