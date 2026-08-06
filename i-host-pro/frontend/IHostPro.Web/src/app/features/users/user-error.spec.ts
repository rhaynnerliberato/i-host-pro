import { classifyUserActionError } from './user-error';

describe('classifyUserActionError', () => {
  it('extracts status and codes from a deserialized 400 ProblemDetails-shaped object', () => {
    expect(classifyUserActionError({ status: 400, title: 'validation_failed', codes: ['EMAIL_INVALID'] })).toEqual({
      status: 400,
      codes: ['EMAIL_INVALID'],
    });
  });

  it('extracts status with an empty codes array for a 404 (never a code-specific reason)', () => {
    expect(classifyUserActionError({ status: 404, title: 'not_found' })).toEqual({ status: 404, codes: [] });
  });

  it('extracts status with an empty codes array for a 409 (never a code-specific reason)', () => {
    expect(classifyUserActionError({ status: 409, title: 'conflict' })).toEqual({ status: 409, codes: [] });
  });

  it('recognizes an ApiException-like object (Error subclass with a status field)', () => {
    class ApiExceptionLike extends Error {
      status: number;
      constructor(status: number) {
        super('Conflict');
        this.status = status;
      }
    }
    expect(classifyUserActionError(new ApiExceptionLike(409))).toEqual({ status: 409, codes: [] });
  });

  it('ignores a non-array codes field instead of throwing', () => {
    expect(classifyUserActionError({ status: 400, codes: 'not-an-array' })).toEqual({ status: 400, codes: [] });
  });

  it('filters out non-string entries from the codes array', () => {
    expect(classifyUserActionError({ status: 400, codes: ['VALID_CODE', 123, null] })).toEqual({
      status: 400,
      codes: ['VALID_CODE'],
    });
  });

  it('treats an object with no status field as having an undefined status', () => {
    expect(classifyUserActionError({ message: 'boom' })).toEqual({ status: undefined, codes: [] });
  });

  it('never throws for null/undefined/string/number values and treats them as unclassified', () => {
    expect(classifyUserActionError(null)).toEqual({ status: undefined, codes: [] });
    expect(classifyUserActionError(undefined)).toEqual({ status: undefined, codes: [] });
    expect(classifyUserActionError('some string')).toEqual({ status: undefined, codes: [] });
    expect(classifyUserActionError(42)).toEqual({ status: undefined, codes: [] });
  });
});
