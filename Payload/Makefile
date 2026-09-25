all: payload.bin

clean:
	rm -f payload.elf payload.bin

lib/lib.a:
	cd lib; make

payload.elf: lib/lib.a main.c
	gcc -g -mcmodel=small -isystem freebsd-headers -nostdinc -nostdlib -fno-stack-protector -static lib/lib.a main.c info.c ps4-libjbc/*.c -Wl,-gc-sections -o payload.elf -fPIE -ffreestanding

payload.bin: payload.elf
	objcopy payload.elf --only-section .text --only-section .data --only-section .bss --only-section .rodata -O binary payload.bin
	file payload.bin | fgrep -q 'payload.bin: DOS executable (COM)'

# EXPERIMENTAL payload: free-space query (cmd 3) + optional storage choice (cmd 2).
# Separate sources; payload.bin is not affected. Build with: make experimental
experimental: payload_experimental.bin

clean-experimental:
	rm -f payload_experimental.elf payload_experimental.bin

payload_experimental.elf: lib/lib.a main_experimental.c info_experimental.c struct_experimental.h
	gcc -g -mcmodel=small -isystem freebsd-headers -nostdinc -nostdlib -fno-stack-protector -static lib/lib.a main_experimental.c info_experimental.c ps4-libjbc/*.c -Wl,-gc-sections -o payload_experimental.elf -fPIE -ffreestanding

payload_experimental.bin: payload_experimental.elf
	objcopy payload_experimental.elf --only-section .text --only-section .data --only-section .bss --only-section .rodata -O binary payload_experimental.bin
	file payload_experimental.bin | fgrep -q 'payload_experimental.bin: DOS executable (COM)'

