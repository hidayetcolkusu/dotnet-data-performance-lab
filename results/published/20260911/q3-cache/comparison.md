# Cache off/on load comparison

Status: **valid**

## Runs

| Repetition | Order | Arm | Requests | avg ms | med ms | p95 ms | p99 ms | Valid |
| ---: | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| 1 | AB | cache-off | 1201 | 1.65 | 1.58 | 2.47 | 3.18 | yes |
| 1 | AB | cache-on | 1201 | 2.76 | 2.68 | 4.38 | 6.47 | yes |
| 2 | BA | cache-off | 1200 | 1.71 | 1.59 | 2.60 | 3.39 | yes |
| 2 | BA | cache-on | 1201 | 2.70 | 2.63 | 4.26 | 5.23 | yes |
| 3 | AB | cache-off | 1201 | 1.65 | 1.57 | 2.48 | 4.02 | yes |
| 3 | AB | cache-on | 1200 | 2.63 | 2.59 | 3.93 | 4.75 | yes |
| 4 | BA | cache-off | 1200 | 1.67 | 1.60 | 2.44 | 3.25 | yes |
| 4 | BA | cache-on | 1200 | 2.70 | 2.62 | 4.15 | 5.64 | yes |
| 5 | AB | cache-off | 1201 | 1.72 | 1.62 | 2.60 | 4.16 | yes |
| 5 | AB | cache-on | 1200 | 2.70 | 2.61 | 4.06 | 6.12 | yes |

## Across repetitions

Percentiles are **not** pooled across runs. Each column below is a statistic *of the
per-run statistic*, over the valid runs of that arm.

| Arm | Valid runs | Requests | median of run p95 ms | min run p95 ms | max run p95 ms | median of run median ms |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| cache-off | 5/5 | 6003 | 2.48 | 2.44 | 2.60 | 1.59 |
| cache-on | 5/5 | 6002 | 4.15 | 3.93 | 4.38 | 2.62 |
