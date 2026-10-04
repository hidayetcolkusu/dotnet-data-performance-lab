# Cache off/on load comparison

Status: **valid**

## Runs

| Repetition | Order | Arm | Requests | avg ms | med ms | p95 ms | p99 ms | Valid |
| ---: | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| 1 | AB | cache-off | 1201 | 1.70 | 1.61 | 2.56 | 3.99 | yes |
| 1 | AB | cache-on | 1200 | 2.72 | 2.62 | 4.23 | 5.43 | yes |
| 2 | BA | cache-off | 1201 | 1.70 | 1.62 | 2.57 | 4.03 | yes |
| 2 | BA | cache-on | 1201 | 2.72 | 2.62 | 4.34 | 5.75 | yes |
| 3 | AB | cache-off | 1200 | 1.61 | 1.55 | 2.37 | 2.90 | yes |
| 3 | AB | cache-on | 1200 | 2.70 | 2.62 | 4.15 | 6.48 | yes |
| 4 | BA | cache-off | 1201 | 1.65 | 1.57 | 2.48 | 3.31 | yes |
| 4 | BA | cache-on | 1201 | 2.73 | 2.73 | 4.06 | 4.88 | yes |
| 5 | AB | cache-off | 1200 | 1.71 | 1.64 | 2.59 | 3.36 | yes |
| 5 | AB | cache-on | 1200 | 2.83 | 2.70 | 4.27 | 8.76 | yes |

## Across repetitions

Percentiles are **not** pooled across runs. Each column below is a statistic *of the
per-run statistic*, over the valid runs of that arm.

| Arm | Valid runs | Requests | median of run p95 ms | min run p95 ms | max run p95 ms | median of run median ms |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| cache-off | 5/5 | 6003 | 2.56 | 2.37 | 2.59 | 1.61 |
| cache-on | 5/5 | 6002 | 4.23 | 4.06 | 4.34 | 2.62 |
