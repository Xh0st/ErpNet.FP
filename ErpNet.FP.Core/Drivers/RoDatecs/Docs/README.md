# Documentație protocol Datecs RO (necesară pentru bonul cu CUI cumpărător)

Pentru implementarea bonului fiscal cu CUI cumpărător (factură simplificată) pe Datecs RO,
trebuie puse aici PDF-urile oficiale de protocol și citate comanda + pagina în cod:

- **Datecs programmer's manual, varianta RO**, pentru FP-700X / DP-25X / WP-500X (și FMP-350X).
  Din el trebuie confirmate, pentru firmware-ul românesc:
  - comanda **48 (0x30) „Open fiscal receipt”** — flagul `Invoice` = `'I'` pentru bon tip factură,
    și ordinea câmpurilor pentru varianta RO;
  - comanda **57 (0x39) „Enter and print invoice data”** — câmpurile
    `{Seller}{Receiver}{Buyer}{Address1}{Address2}{TypeTAXN}{TAXN}{VATN}`:
    - **codul `TypeTAXN` pentru CUI românesc** (în varianta bulgară: 0 = BULSTAT, 1 = EGN, 2 = LNCH,
      3 = service — pentru RO trebuie codul corect pentru CUI persoană juridică, NU se ghicește);
    - **lungimea maximă a `TAXN`** pe firmware RO (bulgară: 8–13 cifre; CUI RO are 2–10 cifre);
    - ordinea comenzii 57 față de vânzări (49) și plăți (53);
    - ce tipărește casa (rândurile cu CUI / denumire cumpărător).

Manualul englez pan-model pentru aceleași aparate există deja în `../../BgDatecs/Docs/`
(`PM_FMP350X_FMP55X_FP700X-BUL_ FPprotocol_eng.pdf`, `bg.isl.dt.x.protocol-v2.02-en.pdf`),
dar acolo `TypeTAXN`/`TAXN` sunt pentru Bulgaria. Varianta RO trebuie confirmată înainte de a trimite
CUI-ul la casă, pentru că un cod sau o lungime greșită scrie un identificator fiscal greșit în bon.
