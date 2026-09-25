/*
 * EXPERIMENTAL GoldHEN installer payload (payload_experimental.bin).
 * Adds free-space query (cmd 3) and an optional storage request (cmd 2).
 * The default payload (main.c -> payload.bin) is unchanged.
 */
#include <stddef.h>
#include <sys/mman.h>
#include "ps4-libjbc/jailbreak.h"
#include "struct_experimental.h"

asm("clear_stack:\nmov $0x800,%ecx\nxor %rax, %rax\n.L1:\npush %rax\nloop .L1\nadd $0x4000,%rsp\nret");
void clear_stack(void);

typedef int (*register_task_fn)(struct bgft_download_param*, int*);
typedef int (*register_task_ex_fn)(struct bgft_download_param_ex*, int*);

static void int32ToHex(int32_t Value, char* Hex)
{
	Hex[0] = '0';
	Hex[1] = 'x';

	for (int i = 7, x = 2; i >= 0; i--, x++) {
		int BytePart = (Value >> (4 * i)) & 0x0F;
		if (BytePart < 10)
			Hex[x] = '0' + BytePart;
		else
			Hex[x] = 'A' + (BytePart - 10);
	}

	Hex[10] = 0;
}

static void concat(const char* StringA, const char* StringB, char* Output) {
	int Offset = 0;
	for (int i = 0; StringA[i] != 0; i++) {
		Output[Offset++] = StringA[i];
	}
	for (int i = 0; StringB[i] != 0; i++) {
		Output[Offset++] = StringB[i];
	}

	Output[Offset] = 0;
}

typedef int (*notify_fn)(int, const char*);

static void notify_code(notify_fn notify, const char* prefix, int rv) {
	char err[0x100];
	char errCode[0x20];
	int32ToHex(rv, errCode);
	concat(prefix, errCode, err);
	notify(222, err);
}

int main()
{
	struct jbc_cred cred;
	jbc_get_cred(&cred);
	jbc_jailbreak_cred(&cred);

	cred.jdir = 0;
	cred.sceProcType = 0x3800000000000010;
	cred.sonyCred = 0x40001c0000000000;
	cred.sceProcCap = 0x900000000000ff00;
	jbc_set_cred(&cred);

	clear_stack();

	void* libSceSysUtil = dlopen("/system/common/lib/libSceSysUtil.sprx", 0);
	notify_fn notify = dlsym(libSceSysUtil, "sceSysUtilSendSystemNotificationWithText");

	void* usrsrv = dlopen("/system/common/lib/libSceUserService.sprx", 0);
	int(*sceUserServiceInitialize)(OrbisUserServiceInitializeParams * user_id) = dlsym(usrsrv, "sceUserServiceInitialize");
	int(*sceUserServiceGetForegroundUser)(int* user_id) = dlsym(usrsrv, "sceUserServiceGetForegroundUser");
	int(*sceUserServiceTerminate)(void) = dlsym(usrsrv, "sceUserServiceTerminate");

	void* bgft = dlopen("/system/common/lib/libSceBgft.sprx", 0);

	int(*sceBgftInitialize)(struct bgft_init_params*) = dlsym(bgft, "sceBgftServiceIntInit");

	register_task_fn sceBgftDownloadRegisterTask = dlsym(bgft, "sceBgftServiceDownloadRegisterTask");
	register_task_fn sceBgftDebugDownloadRegisterTask = dlsym(bgft, "sceBgftServiceIntDebugDownloadRegisterPkg");
	int(*sceBgftDownloadStartTask)(int) = dlsym(bgft, "sceBgftServiceIntDownloadStartTask");

	/* EXPERIMENTAL: may be missing; only used when the PC asks for a storage */
	register_task_ex_fn sceBgftRegisterTaskByStorageEx = dlsym(bgft, "sceBgftServiceIntDownloadRegisterTaskByStorageEx");
	if (!sceBgftRegisterTaskByStorageEx)
		sceBgftRegisterTaskByStorageEx = dlsym(bgft, "sceBgftServiceDownloadRegisterTaskByStorageEx");
	if (!sceBgftRegisterTaskByStorageEx)
		sceBgftRegisterTaskByStorageEx = dlsym(bgft, "sceBgftDownloadRegisterTaskByStorageEx");

	clear_stack();

	struct OrbisUserServiceInitializeParams init_params = {
		.priority = ORBIS_KERNEL_PRIO_FIFO_NORMAL
	};

	int uid = 0;
	sceUserServiceInitialize(&init_params);
	sceUserServiceGetForegroundUser(&uid);
	sceUserServiceTerminate();

	void* libSceAppInstUtil = dlopen("/system/common/lib/libSceAppInstUtil.sprx", 0);
	int(*sceAppInstUtilInitialize)(void) = dlsym(libSceAppInstUtil, "sceAppInstUtilInitialize");

	int rv;

	rv = sceAppInstUtilInitialize();

	if (rv) {
		notify_code(notify, "DPI: App Inst Util Error ", rv);
		return -1;
	}

	struct bgft_init_params ip = {
		.mem = mmap(NULL, 0x100000, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0),
		.size = 0x100000,
	};

	rv = sceBgftInitialize(&ip);

	if (rv && rv != 0x80990001) {
		notify_code(notify, "DPI: BGFT Init Failed ", rv);
		return -1;
	}

	/* request strings live here for the whole loop (see struct_experimental.h) */
	struct pkg_buffers bufs;

	struct bgft_download_param bgft_params = {
		.user_id = uid,
		.entitlement_type = 5,
		.id = "",
		.content_url = "",
		.content_name = "",
		.icon_path = "",
		.package_type = "PS4GD",
		.package_sub_type = "",
		.playgo_scenario_id = "0",
		.option = BGFT_TASK_OPTION_DISABLE_CDN_QUERY_PARAM
	};

	while (1) {
		int storage = STORAGE_DEFAULT;

		rv = get_pkg_info(&bgft_params, &bufs, &storage);

		if (rv == INFO_ERROR) {
			notify(222, "DPI: GET INFO ERROR");
			return -1;
		}

		if (rv == INFO_EXIT)
			break;

		if (rv == INFO_HANDLED)
			continue;

		int task = BGFT_INVALID_TASK_ID;

		/*
		 * EXPERIMENTAL storage selection: pass the storage as the "slot" of
		 * SceBgftDownloadParamEx. Undocumented; on any failure fall back to
		 * the normal registration (console default storage).
		 */
		if (storage == STORAGE_INTERNAL || storage == STORAGE_EXTENDED) {
			if (!sceBgftRegisterTaskByStorageEx) {
				notify(222, "DPI: Storage choice not supported, using the console default");
			} else {
				struct bgft_download_param_ex ex;
				ex.param = bgft_params;
				ex.slot = (unsigned int)storage;

				rv = sceBgftRegisterTaskByStorageEx(&ex, &task);
				if (rv == 0 && task != BGFT_INVALID_TASK_ID) {
					sceBgftDownloadStartTask(task);
					continue;
				}

				task = BGFT_INVALID_TASK_ID;
				if (rv != 0x80990088) /* already installed: the default path reports it */
					notify_code(notify, "DPI: Storage choice failed, using the console default ", rv);
			}
		}

		rv = sceBgftDownloadRegisterTask(&bgft_params, &task);

		if (rv != 0x80990088 && task != BGFT_INVALID_TASK_ID) {
			rv = sceBgftDownloadStartTask(task);
			continue;
		}

		rv = sceBgftDebugDownloadRegisterTask(&bgft_params, &task);

		if (rv != 0x80990088 && task != BGFT_INVALID_TASK_ID) {
			rv = sceBgftDownloadStartTask(task);
			continue;
		}

		if (rv == 0x80990086) {
			notify(222, "DPI: BGFT Error 0x80990086\nEnsure that there are no old downloads in the notification list.");
			continue;
		}

		if (rv == 0x80990088) {
			notify(222, "DPI: Package Already Installed!");
			continue;
		}

		if (rv == 0x80990039 || rv == 0x80A30026) {
			notify(222, "DPI: Insufficient storage space.\nPlease free up space on your hard drive.");
			continue;
		}

		if (rv == 0x80990085) {
			notify(222, "DPI: Insufficient storage space.\nPlease free up non fragmented space on your hard drive.");
			continue;
		}

		notify_code(notify, "DPI: BGFT Error ", rv);
		return -1;
	}

	notify(222, "DirectPackageInstaller Exited");
	return 0;
}
