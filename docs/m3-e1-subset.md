# M3 E1 alt kümesi (m3-e1 v1)

- Durum: **Kullanıcı tarafından kabul edildi (2026-10-16, U).** Kapı C, kullanıcının etkileşimli koşusuyla tamamlandı (`scripts/m3-e1-import.ps1 -AllowNetwork`, rapor `m3-e1-subset-20261015T223031Z.md`; ham veri ve tam rapor repo dışında, arşiv önbelleğinde).
- Snapshot hash: `b909f280d4494fc1406cfa479f52a09096352612ed5a4495f93094329e14234b`
- H1 (dev DB'de kesim sonrası mum): **0** (`-CheckCutoff`).
- Kurallar: `m3-protocol.md` §11 (ADR-0012 kesinti istisnası uygulanmaz; `< U0` yalnızca ısınma). Sıralama: U0 sonrası ilk 180 günün medyan günlük quote hacmi.
- Amaç: boru hattı doğrulaması. **Araştırma evreni değildir.**
- Arşivde listelenen sembol dizini: 3745; dışlamalardan sonra çekirdek dışı USDT sembolü: 660; E1 adayı: 156; elenen: 0.

| Sıra | Sembol | İlk açılış (UTC) | U0 | Etiket | 1h eksik | 4h eksik | 1h/4h anomali |
|---|---|---|---|---|---|---|---|
| 1 | WIFUSDT | 2024-03-05 14:00 | 2024-04-04 14:00 | E1-1y | 0 | 0 | 0 |
| 2 | ORDIUSDT | 2023-11-07 12:00 | 2023-12-07 12:00 | E1-2y | 0 | 0 | 0 |
| 3 | GMTUSDT | 2022-03-09 12:00 | 2022-04-08 12:00 | E1-3y | 1 | 0 | 1 |
| 4 | BONKUSDT | 2023-12-15 08:00 | 2024-01-14 08:00 | E1-1y | 0 | 0 | 0 |
| 5 | BOMEUSDT | 2024-03-16 12:00 | 2024-04-15 12:00 | E1-1y | 0 | 0 | 0 |

GMTUSDT'deki 1 eksik 1h mum ve 1 adet 1h/4h anomalisi eşikler içindedir. Raporlandı, düzeltilmedi.
