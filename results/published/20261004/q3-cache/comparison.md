# Cache off/on load comparison

Status: **valid**

## Runs

| Repetition | Order | Arm | Requests | avg ms | med ms | p95 ms | p99 ms | Valid |
| ---: | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| 1 | AB | cache-off | 1201 | 3.25 | 3.01 | 5.74 | 8.32 | yes |
| 1 | AB | cache-on | 1201 | 4.93 | 4.80 | 8.97 | 16.22 | yes |
| 2 | BA | cache-off | 1201 | 2.82 | 2.61 | 4.71 | 7.47 | yes |
| 2 | BA | cache-on | 1201 | 5.30 | 4.37 | 10.95 | 19.78 | yes |
| 3 | AB | cache-off | 1201 | 2.35 | 2.09 | 4.63 | 6.93 | yes |
| 3 | AB | cache-on | 1201 | 3.97 | 3.61 | 7.69 | 11.56 | yes |
| 4 | BA | cache-off | 1200 | 2.56 | 2.20 | 4.75 | 7.14 | yes |
| 4 | BA | cache-on | 1200 | 3.87 | 3.76 | 7.36 | 10.30 | yes |
| 5 | AB | cache-off | 1201 | 1.90 | 1.82 | 2.85 | 4.01 | yes |
| 5 | AB | cache-on | 1201 | 2.96 | 3.07 | 4.70 | 6.45 | yes |

## Across repetitions

Percentiles are **not** pooled across runs. Each column below is a statistic *of the
per-run statistic*, over the valid runs of that arm.

| Arm | Valid runs | Requests | median of run p95 ms | min run p95 ms | max run p95 ms | median of run median ms |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| cache-off | 5/5 | 6004 | 4.71 | 2.85 | 5.74 | 2.20 |
| cache-on | 5/5 | 6004 | 7.69 | 4.70 | 10.95 | 3.76 |
