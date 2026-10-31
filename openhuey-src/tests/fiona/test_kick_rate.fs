\ Focused kick timing trace. Run with:
\   openhuey --eval "30 sim-rate!" --test tests/fiona/test_kick_rate.fs
\   openhuey --eval "60 sim-rate!" --test tests/fiona/test_kick_rate.fs
IN: test-fiona-kick-rate
USING: tester engine game-state actors messages room-names game fiona.state fiona.anims fiona.model fiona.moving fiona.defend debug ;

: her@ ( xt -- x )  fiona enter execute leave-actor ;
: her-model ( -- a ) [: her-model @ ;] her@ ;
: her-frame ( -- F: frame ) her-model actor act.frame sf@ ;
: her-anim ( -- n ) [: anim@ ;] her@ ;
: her-xz ( -- v ) fiona body-at ;
: press-key ( key -- ) dup true key-hold game-tick false key-hold ;
: print-step ( n -- )
    ." kick step " . ." anim " her-anim . ." time " her-frame f.
    ." root " her-model root-delta f. f. f. f.
    ." pos " her-xz vec@ f. f. f. cr ;
: trace ( -- )
    90 0 do i print-step game-tick loop ;

2 run-frames
0 fiona send costume 1 run-frames
game:story send story-stop deliver
progress pr.state 8 0 fill
castle-1f-1 -1 rooms send go-to-room 20 run-frames

[: room-id 439 dup tri-center body-place self body-at her-at vec-copy self body-tri her-tri ! ;] fiona
fiona true noclip
[: pi her-yaw f! 0 ;] her@ drop
key: Z press-key
1 run-frames
trace

test-summary
