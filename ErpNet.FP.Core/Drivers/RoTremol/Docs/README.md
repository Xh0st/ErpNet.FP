# Documentație protocol Tremol RO (necesară pentru bonul cu CUI cumpărător)

Pentru implementarea bonului fiscal cu CUI cumpărător (factură simplificată) pe Tremol RO,
trebuie pus aici PDF-ul oficial de protocol și citate comanda + pagina în cod:

- **Tremol „Communication protocol” RO** (din ZFP SDK RO, ex. `protocol_description_RO_2601151250.pdf`).
  Din el trebuie confirmate, pentru firmware-ul românesc:
  - comanda de **deschidere a bonului în regim factură** (sau flagul din `OpenReceipt`, cmd 0x30,
    `FiscalReceiptPrintType`) care marchează documentul ca factură;
  - comanda care **trimite datele cumpărătorului (CUI / denumire / adresă)** — numărul comenzii ZFP,
    ordinea câmpurilor, separatorii și **lungimea maximă** a CUI-ului;
  - ordinea acestei comenzi față de vânzări (0x31) și plăți (0x35);
  - ce tipărește casa.

Protocolul ZFP bulgăresc există deja în `../../BgTremol/Docs/protocol_description_BG_1910211454.pdf`
(cadru identic STX…ETX), dar comanda de date cumpărător și lungimile trebuie confirmate pe varianta RO
înainte de a trimite CUI-ul la casă.
