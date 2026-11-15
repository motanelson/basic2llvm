REM Greatest Common Divisor (Euclidean)
PRINT "Enter first number (A):"
INPUT a
PRINT "Enter second number (B):"
INPUT b

euclid:
IF b = 0 THEN GOTO finish
temp = b
b = a % b
a = temp
GOTO euclid

finish:
PRINT "GCD is:"
PRINT a
END