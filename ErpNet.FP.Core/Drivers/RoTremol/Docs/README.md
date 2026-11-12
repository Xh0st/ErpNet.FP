# Documentație protocol Tremol RO — bon fiscal cu CUI cumpărător

Sursa oficială: **Tremol „Communication protocol” RO**, din ZFP SDK RO
(`protocol_description_RO_2601151250.pdf`, în acest folder).

## Bonul cu CUI se deschide prin comanda 30h „special customer document”

Protocolul are două variante ale comenzii **30h** (secțiunea 2.6):

* **2.6.3, p.44 — „Open fiscal receipt”** (bon normal):
  `OperNum ; OperPass[4] ; '0' ; '0' ; FiscalReceiptPrintType` — PrintType `'2'` = postponed.

* **2.6.4, p.46 — „Open fiscal special customer document”** (bon cu CUI cumpărător):
  ```
  OperNum ; OperPass[4] ; '0' ; '0' ; CustomerReceiptPrintType ;
  CustomerVATNum[15] ; InvRecipient[30] ; InvFree1[20] ; InvFree2[20] ;
  InvFree3[20] ; InvFree4[20] ; Address[30]
  ```
  * `CustomerReceiptPrintType`: `'1'` step-by-step, `'3'` postponed, `'5'` buffered — driverul folosește `'3'`.
  * `CustomerVATNum[15]`: **CIF-ul cumpărătorului** (plătitor TVA → `RO<cui>`; neplătitor → CUI simplu).
  * `InvRecipient[30]`: denumirea cumpărătorului.
  * `InvFree1..4[20]`: linii libere (goale în implementarea curentă).
  * `Address[30]`: adresa cumpărătorului.

Restul documentului fiscal este identic cu bonul normal:
sell `31h` (2.6.5, p.46), subtotal `33h` (2.6.10, p.50), plată `35h` (2.6.13, p.53),
închidere `38h` (2.6.17, p.55). De aceea `PrintInvoice` refolosește întreg fluxul `PrintReceipt`,
schimbând doar deschiderea bonului. Nicio comandă nu a fost ghicită.

Numărul facturii este atribuit de casă (se citește cu `71h`, 2.5.x) și este egal cu numărul bonului fiscal.

## Referință suplimentară

Pe calculatorul de test există și integrarea legacy `C:\ServerTremol` (driver pe fișiere), cu exemple
reale de bonuri cu CUI (`Exemple Bonuri\BonuriCui`, `Sample Receipts\Receipts with VAT of Clients`) —
confirmă comportamentul, dar formatul lor e de nivel înalt, nu comanda ZFP brută de mai sus.
