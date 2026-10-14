# M1 Çekirdek Evren (core-4y-S)

- Durum: **Kullanıcı onaylı (2026-10-14).** M2 bu listeyle başlar; değişiklik yeni bir politika sürümü ve onay gerektirir.
- Politika: `m1-data-policy.md` v1.1 · Snapshot hash: `a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82` (`market.dataset_snapshots`)
- Evren etiketi: **`core-4y-S`** — C'ye kadar kesintisiz veri koşuluyla seçildi (survivor-conditioned). Arşivin delist edilmiş sembolleri tutup tutmadığı doğrulanmadı; havuz bugünkü arşivde görünen sembollerle sınırlıdır. Hayatta kalma yanlılığının giderildiği iddia edilmez.
- Kaynak: kullanıcının paylaştığı M1 kabul raporu. Bu belge yalnızca sembol adları, sıralar ve özet sayılar içerir; mum verisi içermez.

## Özet
| Ölçü | Değer |
|---|---|
| Arşivdeki sembol dizini | 3.745 (desteklenmeyen ad, atlandı: 7) |
| USDT sembolü | 755 |
| Çekirdek havuzu (`first_open_arşiv ≤ 2021-12-02`) | 303 |
| Sıralanan | 260 |
| Dosyalar | Imported 4.100 · AlreadyImported 72 · Missing 305 · Rejected 5 |

## Çekirdek 20 parite
| Sıra | Sembol | Sıra | Sembol |
|---|---|---|---|
| 1 | BTCUSDT | 13 | FILUSDT |
| 2 | ETHUSDT | 15 | GALAUSDT |
| 3 | BNBUSDT | 16 | AXSUSDT |
| 4 | SOLUSDT | 17 | LINKUSDT |
| 5 | XRPUSDT | 18 | TRXUSDT |
| 6 | DOTUSDT | 19 | LTCUSDT |
| 7 | ADAUSDT | 20 | VETUSDT |
| 8 | DOGEUSDT | 22 | SANDUSDT |
| 9 | AVAXUSDT | 23 | THETAUSDT |
| 11 | SHIBUSDT | 24 | DYDXUSDT |

## Hayatta kalma yanlılığı: elenenler
| Sıra | Sembol | Neden |
|---|---|---|
| 10 | LUNAUSDT | InternalGap |
| 12 | MATICUSDT | Ended |
| 14 | FTMUSDT | Ended |
| 21 | EOSUSDT | Ended |

`Ended` nedeni (delist, ad değişikliği ya da arşiv kesilmesi) bu veriyle ayırt edilemez.

## Kabul edilen gözlemler (U)
- Araştırma penceresinde her çekirdek paritede 1 eksik 1h mum ve buna bağlı 1 doğrulanamaz 4h blok; 1h/4h anomali sayısı 0 ya da 1. Hepsi politika eşikleri içinde (≥ %99, tek boşluk ≤ 72 saat). Kullanıcı bunu piyasa genelindeki bir bakım saati olarak değerlendirdi; neden bağımsız olarak doğrulanmadı.
- `REVIEW-PENDING`: JUPUSDT, SYRUPUSDT (çekirdeğe girmedi).

## Açık konu (M2 öncesi)
SOLUSDT, TRXUSDT ve SANDUSDT'nin **Aralık 2021 1h** dosyaları `close_time` tutarlılık kontrolü nedeniyle reddedildi. Bu dosyalar ısınma (warm-up) aralığındadır; araştırma penceresi ve seçim etkilenmedi. 4h ve 1d ısınma verisi etkilenmedi. Isınmanın nasıl ele alınacağı M2 başlamadan karara bağlanmalıdır (CHANGELOG 2026-10-14 "M1 kapanışı").
