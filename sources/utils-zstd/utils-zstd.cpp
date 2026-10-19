#include <cstdint>
#include <zstd.h>
#include <utils-zstd/utils-zstd.h>

_ZSTD_SHARED_API const char* DecompressZSTD(size_t downloadedDataSize, const uint8_t* downloadedData, uint8_t* dest, int destSeek, int destSize)
{
	const char* error = nullptr;

	ZSTD_DCtx* const dctx = ZSTD_createDCtx();

	if (dctx == nullptr)
	{
		error = "ZSTD_createDCtx() failed!";
	}
	else
	{
		long long totalOutput = 0;
		ZSTD_inBuffer input = { downloadedData, downloadedDataSize, 0 };
		while (totalOutput <= destSeek && input.pos < input.size)
		{
			ZSTD_outBuffer outputSeek = { dest, static_cast<size_t>(destSize), static_cast<size_t>(0) };
			size_t const ret = ZSTD_decompressStream(dctx, &outputSeek, &input);
			if (ZSTD_isError(ret) || static_cast<long long>(outputSeek.pos) != destSize)
			{
				error = "Error during decompression";
				break;
			}
			totalOutput += static_cast<long long>(outputSeek.pos);
		}
		ZSTD_freeDCtx(dctx);
	}
	
	return error;
}
