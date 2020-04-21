#include "pch.h"
#include "BlowFishClassLibrary.h"
using namespace BlowFishClassLibrary;

// TODO: Add your methods for this class here.
DWORD BlowFishClass::Encode(BYTE key[], int keybytes, BYTE pInput[], BYTE* pOutput, DWORD lSize) {
	return blowFish->Encode(key, keybytes, pInput, pOutput, lSize);
};

void BlowFishClass::Decode(BYTE key[], int keybytes, BYTE pInput[], BYTE* pOutput, DWORD lSize) {

	blowFish->Decode(key, keybytes, pInput, pOutput, lSize);
};