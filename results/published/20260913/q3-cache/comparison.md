# Cache off/on load comparison

Status: **valid**

## Runs

| Repetition | Order | Arm | Requests | avg ms | med ms | p95 ms | p99 ms | Valid |
| ---: | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| 1 | AB | cache-off | 1201 | 2.68 | 2.30 | 5.10 | 8.52 | yes |
| 1 | AB | cache-on | 1201 | 4.35 | 3.83 | 8.25 | 14.66 | yes |
| 2 | BA | cache-off | 1201 | 2.18 | 1.99 | 3.69 | 6.10 | yes |
| 2 | BA | cache-on | 1201 | 3.83 | 3.66 | 7.50 | 12.70 | yes |
| 3 | AB | cache-off | 1201 | 2.17 | 1.93 | 3.84 | 7.03 | yes |
| 3 | AB | cache-on | 1201 | 3.65 | 3.32 | 7.57 | 10.87 | yes |
| 4 | BA | cache-off | 1200 | 2.16 | 1.93 | 4.12 | 6.47 | yes |
| 4 | BA | cache-on | 1200 | 3.53 | 3.22 | 7.63 | 11.41 | yes |
| 5 | AB | cache-off | 1200 | 2.21 | 2.01 | 3.96 | 6.10 | yes |
| 5 | AB | cache-on | 1201 | 4.39 | 3.85 | 8.60 | 14.87 | yes |

## Across repetitions

Percentiles are **not** pooled across runs. Each column below is a statistic *of the
per-run statistic*, over the valid runs of that arm.

| Arm | Valid runs | Requests | median of run p95 ms | min run p95 ms | max run p95 ms | median of run median ms |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| cache-off | 5/5 | 6003 | 3.96 | 3.69 | 5.10 | 1.99 |
| cache-on | 5/5 | 6004 | 7.63 | 7.50 | 8.60 | 3.66 |
