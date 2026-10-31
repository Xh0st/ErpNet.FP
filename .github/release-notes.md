Prima versiune ErpNet.FP pentru casele de marcat din România.

**Pre-release:** formatul comenzilor vine dintr-un fix verificat pe un Datecs DP-25 românesc. Modelele MX și comentariile pe bon trebuie confirmate pe aparat cu `tools\test-ro.ps1` înainte de folosirea la clienți.

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

1. Descărcați `ErpNet.FP-RO-<versiune>-win-x64.zip` și dezarhivați, de exemplu în `C:\ErpNet.FP`.
2. Setați `appsettings.json` (exemplu în `README-RO.md`), de exemplu `"Uri": "ro.dt.x.isl.com://COM3"`.
3. Treceți casa în modul 6 „Conexiune PC” și porniți `ErpNet.FP.Server.exe` (sau instalați-l ca serviciu Windows).
4. Pagina de administrare: http://localhost:8001.

Nu este nevoie de .NET instalat. Sunt incluse și build-uri pentru Windows 32 bit, Linux x64 și Linux ARM.
