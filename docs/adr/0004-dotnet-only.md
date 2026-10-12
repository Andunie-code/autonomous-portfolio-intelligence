# ADR-0004: Tamamen .NET (Python worker yok)

- Durum: Kabul edildi · Tarih: 2026-10-12

## Bağlam
Python'un istatistik ekosistemi güçlü. Öte yandan canlı sistem ile backtest'in aynı sonucu vermesi (parity) en kritik doğruluk gereksinimi.

## Karar
İndikatörler, backtest, değerlendirme ve istatistik hesapları C# ile yazılır. Python yalnızca ileride gerekirse offline notebook analizi için kullanılabilir.

## Sonuçlar
- Tek dil ve tek implementasyon olur; dil sınırında sessiz farklar oluşmaz.
- Bedeli: Deflated Sharpe ve bootstrap gibi yöntemler elle yazılır ve test edilir.
- Yeniden değerlendirme tetikleyicisi: ML modelleri gerekirse önce ML.NET/ONNX denenir.
