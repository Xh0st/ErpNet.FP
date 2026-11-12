# Documentație protocol Datecs RO — bon fiscal cu CUI cumpărător

Sursa oficială folosită: **Datecs „DUDE” — For_Developers**, instalat cu aplicația Datecs
(`C:\Program Files (x86)\Datecs Applications\DUDE\For_Developers`), folderul
**`BIN\Scripturi teste avizare`** (scripturile oficiale de avizare Datecs pentru România).
Scripturile relevante sunt copiate în `avizare-datecs-ro/`.

## Bonul cu CUI se deschide prin comanda 48 (nu printr-o comandă separată)

Comanda **48 „Deschidere bon fiscal”** are formatul (câmpuri separate prin TAB):

```
{OpCode}<TAB>{OpPwd}<TAB>{TillNmb}<TAB>{Invoice}<TAB>{CIF}<TAB>
```

* **Bon normal:** `48,1\t0001\t1\t\t\t` — câmpurile `Invoice` și `CIF` goale.
* **Bon fiscal cu factură (CUI cumpărător):** `48,1\t0001\t1\tI\tRO123456789\t`
  — câmpul 4 = `I` (marchează bonul ca factură) și câmpul 5 = **CIF-ul cumpărătorului**.

Confirmat în scripturile de avizare (toate deschid bonul cu `I` + CIF):
- `avizare-datecs-ro/VanzareC-complexa-platitor-TVA.txt`
- `avizare-datecs-ro/VanzareC-complexa-neplatitor-TVA.txt`
- `avizare-datecs-ro/Vanzare-cu-Subtotal.txt`
- `avizare-datecs-ro/Vanzare-forme-de-plata.txt`

Formatul folosit de driverul propriu pe fișiere al Sincrona — `48,1\t0001\t1\tI\t<CUI>\t` —
**este corect** (identic cu scripturile de avizare).

## CIF-ul trimis

În scripturi CIF-ul apare ca `RO123456789` (plătitor de TVA). Driverul trimite:
* **plătitor de TVA:** `vatNumber` (forma `RO<cui>`);
* **neplătitor de TVA:** `identifier` (CUI simplu, fără `RO`).

## Restul fluxului e identic cu bonul normal

Vânzările (49), subtotalul (51), plata (53) și închiderea (56) sunt exact aceleași ca la `/receipt`
(vezi scripturile). De aceea `PrintInvoice` refolosește întreg fluxul `PrintReceipt`, schimbând doar
deschiderea bonului. Nicio comandă nu a fost ghicită.

## Modele

Kit-ul For_Developers acoperă familia Datecs RO X/MX (DP-25X/MX, DP-150MX, WP-50/500MX, FP-700MX,
FMP-350MX) — aceleași pe care le detectează driverul `ro.dt.x.isl`. De aceea `SupportsInvoice = true`
e setat pentru driver, cu `invoiceNumberAssignment = device-assigned`.
