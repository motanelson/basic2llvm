REM Collatz Conjecture (3N + 1)
PRINT "Enter starting number:"
INPUT n
steps = 0

collatz_loop:
PRINT n
IF n = 1 THEN GOTO done
steps = steps + 1

REM Check if even (n % 2 == 0)
rem_val = n % 2
IF rem_val = 0 THEN GOTO is_even

REM Odd step
n = 3 * n + 1
GOTO collatz_loop

is_even:
n = n / 2
GOTO collatz_loop

done:
PRINT "Total steps taken:"
PRINT steps
END