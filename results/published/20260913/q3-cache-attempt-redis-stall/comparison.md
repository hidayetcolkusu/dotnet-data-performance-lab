# Cache off/on load comparison

Status: **invalid**

## Runs

| Repetition | Order | Arm | Requests | avg ms | med ms | p95 ms | p99 ms | Valid |
| ---: | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| 1 | AB | cache-off | 1201 | 6.02 | 4.83 | 14.28 | 25.51 | yes |
| 1 | AB | cache-on | 1201 | 6.21 | 4.72 | 15.04 | 26.69 | yes |
| 2 | BA | cache-off | 1200 | 4.84 | 3.75 | 10.98 | 19.61 | yes |
| 2 | BA | cache-on | - | - | - | - | - | no |
| 3 | AB | cache-off | 1201 | 3.40 | 2.52 | 6.81 | 16.89 | yes |
| 3 | AB | cache-on | 1201 | 5.94 | 4.65 | 13.79 | 32.13 | yes |
| 4 | BA | cache-off | 1201 | 3.64 | 2.67 | 7.67 | 18.87 | yes |
| 4 | BA | cache-on | 1201 | 8.27 | 6.74 | 18.86 | 36.38 | yes |
| 5 | AB | cache-off | 1201 | 4.45 | 3.72 | 9.43 | 16.23 | yes |
| 5 | AB | cache-on | 1201 | 6.12 | 4.69 | 15.10 | 29.97 | yes |

## Across repetitions

Percentiles are **not** pooled across runs. Each column below is a statistic *of the
per-run statistic*, over the valid runs of that arm.

| Arm | Valid runs | Requests | median of run p95 ms | min run p95 ms | max run p95 ms | median of run median ms |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| cache-off | 5/5 | 6004 | 9.43 | 6.81 | 14.28 | 3.72 |
| cache-on | 4/5 | 4804 | 15.04 | 13.79 | 18.86 | 4.69 |

## Why this comparison is invalid

- 'runs/2-BA-cache-on.summary.json': k6 exited 99; its own thresholds did not hold.
- the arms have different valid run counts; the comparison is unbalanced. cache-off=5, cache-on=4
