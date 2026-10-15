# ADR-0003: Stratejiler kod değil, declarative DSL

- Durum: Kabul edildi · Tarih: 2026-10-12

## Bağlam
LLM'in strateji önermesi isteniyor. LLM'in ürettiği kodu çalıştırmak hem güvenlik hem tekrar üretilebilirlik riski taşır.

## Karar
Stratejiler, beyaz listeli indikatör ve operatörlerden oluşan, sığ şemalı bir JSON DSL ile tanımlanır. Interpreter kernel'in parçasıdır. Strateji tanımları immutable ve versiyonludur. M2'deki baseline'lar C# ile yazılır; M3'te DSL karşılıklarıyla parity testi yapılır.

## Sonuçlar
- İfade gücü sınırlıdır; yeni bir indikatör ya da operatör eklemek, insan tarafından incelenen bir kod değişikliğidir.
- Şemanın sığ tutulması, Gemini'nin desteklediği JSON Schema alt kümesiyle de uyumludur (`sources.md` S-G7).

## Revizyon notu (2026-10-16)
Beyaz liste (indikatörler, operatörler, şema ve sınırlar) ADR-0016'dadır (DSL v1).
