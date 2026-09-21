import { classifyAuthActionError } from './auth-action-error';

describe('classifyAuthActionError', () => {
  it('extracts status and codes from a deserialized 400 ProblemDetails-shaped object', () => {
    expect(classifyAuthActionError({ status: 400, title: 'validation_failed', codes: ['PasswordTooShort'] })).toEqual({
      status: 400,
      codes: ['PasswordTooShort'],
    });
  });

  it('extracts the single undifferentiated token-invalid code for reset-password-complete', () => {
    expect(classifyAuthActionError({ status: 400, codes: ['Identity.PasswordResetTokenInvalid'] })).toEqual({
      status: 400,
      codes: ['Identity.PasswordResetTokenInvalid'],
    });
  });

  it('recognizes an ApiException-like object (Error subclass with a status field)', () => {
    class ApiExceptionLike extends Error {
      status: number;
      constructor(status: number) {
        super('Internal Server Error');
        this.status = status;
      }
    }
    expect(classifyAuthActionError(new ApiExceptionLike(500))).toEqual({ status: 500, codes: [] });
  });

  it('ignores a non-array codes field instead of throwing', () => {
    expect(classifyAuthActionError({ status: 400, codes: 'not-an-array' })).toEqual({ status: 400, codes: [] });
  });

  it('filters out non-string entries from the codes array', () => {
    expect(classifyAuthActionError({ status: 400, codes: ['VALID_CODE', 123, null] })).toEqual({
      status: 400,
      codes: ['VALID_CODE'],
    });
  });

  it('never throws for null/undefined/string/number values and treats them as unclassified', () => {
    expect(classifyAuthActionError(null)).toEqual({ status: undefined, codes: [] });
    expect(classifyAuthActionError(undefined)).toEqual({ status: undefined, codes: [] });
    expect(classifyAuthActionError('some string')).toEqual({ status: undefined, codes: [] });
    expect(classifyAuthActionError(42)).toEqual({ status: undefined, codes: [] });
  });
});
