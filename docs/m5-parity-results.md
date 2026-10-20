# M5 — Kohort 0 parity replay sonucu (`Frozen → PaperTrading`)

- Durum: **Geçti (2026-10-20).** Protokol: `m5-protocol.md` §5 (`9dda841`). Engine `81d2596`. Kohort: holdout DB'de cohort 1 (Kohort 0).
- Komutlar (`research_app`, yalnızca `R`):
  - `m5-research.ps1 freeze-import docs/cohorts/cohort-0.json 1`
  - `m5-research.ps1 parity-m5 docs/cohorts/cohort-0.json 1 --record`
- Rapor dosyası (repo dışında): `m5-parity-cohort1-20261020T145528Z.json`, SHA-256 `841067435f6da549a29d5d1e4f6129f8813e18f8ace09b96702b32a5c8e1bea4`. Bu hash lifecycle kaydının kanıtıdır.

## Ön kayıt koşulları
| Koşul | Sonuç |
|---|---|
| Dondurulmuş tanımlar (manifesto hash'i `2099da77…`) | ema-trend `9885d24c…`, supertrend `b3401c52…` |
| Replay | 9.844 adım; 8 saatlik döngüler; 16 yeniden başlatma (90 günde bir); 48 dolum |
| L1 (sinyal) | kernel `f870c0a6…` = paper `f870c0a6…` → **birebir** |
| L2 (karar) | kernel `b078ebaa…` = paper `b078ebaa…` → **birebir** |
| Reconciliation | Her yeniden başlatmada temiz |
| Lifecycle | İki strateji için `Frozen → PaperTrading` yazıldı (kanıt: rapor hash'i) |

## L3 fark raporu (bilgilendirici; doğrulamaya girmez)
Her sleeve, kendi izole tek strateji koşusuyla (evaluator gibi, 10.000 USDT sabit sermaye) karşılaştırıldı. Mutlak tutarlar karşılaştırılmaz.

| Strateji | İşlem (izole / portföy) | Zamanlama eşleşmesi | İşlem başına ort. getiri (izole / portföy) | Portföy kısıtı kararları |
|---|---|---|---|---|
| ema-trend | 10 / 3 | %30,0 | −%7,67 / −%6,43 | drawdown-halt 698, max-positions 2 |
| supertrend | 20 / 21 | %85,0 | −%5,46 / −%4,89 | drawdown-halt 1890, max-positions 25, total-exposure 6 |

- **Kategori:** farkların neredeyse tamamı "ortak sermaye ve portföy kısıtları" kategorisindedir. İki sleeve aynı 10.000 USDT'yi ve aynı portföy düzeyindeki %15 drawdown halt'ını paylaşır. Portföyde halt daha erken ve ortak tetiklendiği için ema-trend'in izole koşudaki girişlerinin çoğu açılmadı.
- **Yorum:** bu bir parity hatası değil, ADR-0015'in öngördüğü bir L3 farkıdır.
- **Diğer kategoriler:** canlı/arşiv mum farkı, dolum ve `MIN_NOTIONAL` kategorilerinde fark yoktur (aynı arşiv mumları, aynı maliyet modeli).
