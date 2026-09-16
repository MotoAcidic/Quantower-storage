#!/usr/bin/env perl
# Applies one mutation to one file, in place, first occurrence only.
#
#     perl mutate.pl <from> <to> <file>
#
#     0  applied
#     2  pattern not found -- the code moved and the mutation is stale
#     3  the file could not be read or written
#
# The source files are CRLF and the patterns in mutate.sh are written LF, so the match is built
# line by line and joined with \r?\n. This is not a detail. A plain multi-line match silently
# fails to apply, `grep -F` treats a multi-line pattern as "any one of these lines" so it does
# not catch the miss either, and the run then reports SURVIVED against code that was never
# mutated. Every multi-line mutation read as a survivor the first time this suite was checked.

use strict;
use warnings;

my ($from, $to, $file) = @ARGV;
die "usage: mutate.pl <from> <to> <file>\n" unless defined $file;

open(my $in, '<', $file) or exit 3;
binmode $in;
my $text = do { local $/; <$in> };
close $in;

my $crlf = $text =~ /\r\n/;
my $pattern = join('\r?\n', map { quotemeta } split(/\n/, $from, -1));

exit 2 unless $text =~ /$pattern/;

my $replacement = $to;
$replacement =~ s/\n/\r\n/g if $crlf;

$text =~ s/$pattern/$replacement/;

open(my $out, '>', $file) or exit 3;
binmode $out;
print $out $text;
close $out;

exit 0;
