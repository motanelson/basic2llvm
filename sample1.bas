REM Fibonacci Sequence Generator
PRINT "Fibonacci Generator"
PRINT "Enter how many terms to generate:"
INPUT count

a = 0
b = 1
idx = 0

print_loop:
IF idx >= count THEN GOTO done
PRINT a
next_val = a + b
a = b
b = next_val
idx = idx + 1
GOTO print_loop

done:
PRINT "Done!"
END